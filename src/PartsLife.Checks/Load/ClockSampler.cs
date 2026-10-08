using System.Runtime.InteropServices;

namespace PartsLife.Checks.Load;

/// <summary>ある1秒の CPU の様子。読めなかった値は null。</summary>
public sealed record ClockSample(double? LoadPercent, double? EffectiveMHz, double? BaseMHz, double? PerformancePercent);

/// <summary>
/// CPU の実効クロックと負荷を PDH から読む。**管理者権限は要らない。**
///
/// 実効クロック = 基準クロック（Processor Frequency）× % Processor Performance / 100。
/// タスクマネージャーの「速度」と同じ出し方。
/// **温度ではない。** 温度はカーネルドライバ無しには読めないので、ここでも出さない。
///
/// カウンタ名は英語名で登録する（日本語版 Windows では表示名が翻訳されているため）。
/// </summary>
public sealed class ClockSampler : IDisposable
{
    private IntPtr _query, _perf, _freq, _load;
    private readonly bool _ready;

    public ClockSampler()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf);
            PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq);
            PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Time", IntPtr.Zero, out _load);
            PdhCollectQueryData(_query);    // 1回目は差分の基準になるだけ
            _ready = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>1秒に1回だけ呼ぶ（PDH は前回との差で値を出す）。</summary>
    public ClockSample Sample()
    {
        if (!_ready || PdhCollectQueryData(_query) != 0) return new ClockSample(null, null, null, null);
        double? perf = Read(_perf), freq = Read(_freq), load = Read(_load);
        double? eff = perf is { } p && freq is { } f && f > 0 ? f * p / 100.0 : null;
        return new ClockSample(load is { } l ? Math.Clamp(l, 0, 100) : null, eff, freq, perf);
    }

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(counter, 0x00000200 /*PDH_FMT_DOUBLE*/ | 0x00008000 /*NOCAP100*/, out _, out var v) != 0)
            return null;
        return v.CStatus is 0 or 1 && !double.IsNaN(v.Value) ? v.Value : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        private readonly uint _pad;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
