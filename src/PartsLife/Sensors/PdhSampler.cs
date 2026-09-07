using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PartsLife.Sensors;

public sealed record GpuInfo(string Key, string Name, double Utilization, ulong VramUsedBytes, ulong VramTotalBytes);

/// <summary>ディスク全体の読み書き速度（バイト/秒）。</summary>
public sealed record DiskThroughput(double ReadBytesPerSec, double WriteBytesPerSec);

/// <summary>
/// GPU の使用率・VRAM と、ディスクの読み書き速度を読む。
///
/// ■ 管理者権限は要らない
/// Windows 10 (1709) 以降の「GPU Engine」「GPU Adapter Memory」カウンタを使う。
/// タスクマネージャーが見ているのと同じ情報で、ベンダーを問わない。
///
/// ■ **英語名のカウンタで登録する（PdhAddEnglishCounter）**
/// 日本語版 Windows ではカウンタ名が翻訳されているため、
/// `\GPU Engine(*)\Utilization Percentage` をそのまま PdhAddCounter に渡すと失敗する。
/// 英語版APIを使えば、どの言語の Windows でも同じパスで通る。
///
/// ■ 使用率の出し方
/// インスタンス名は `pid_123_luid_0x0_0xABCD_phys_0_eng_0_engtype_3D` の形。
/// **エンジンの種類ごとに合計し、その最大を取る。**
/// 全部足すと 3D と Copy が同時に動いたときに 200% を超えてしまう。
/// </summary>
public sealed class PdhSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _utilCounter;
    private IntPtr _memCounter;
    private IntPtr _diskReadCounter;
    private IntPtr _diskWriteCounter;
    private bool _ready;

    private static readonly Regex LuidRe = new(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex EngTypeRe = new(@"engtype_(\w+)$", RegexOptions.Compiled);

    /// <summary>
    /// LUID → 名前と総VRAM。**D3DKMT から1回だけ拾う。**
    /// レジストリの並び順で突き合わせると、外したカードの残骸に当たって別物になる
    /// （経緯は GpuIdentity.cs を参照）。
    /// </summary>
    private readonly Dictionary<string, GpuIdentity> _identity =
        GpuIdentity_.Enumerate().ToDictionary(g => g.LuidKey, g => g);

    public PdhSampler()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _utilCounter) != 0) return;
            // VRAM のカウンタは環境によって無い。失敗しても使用率だけで続ける。
            PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memCounter);
            // ディスクの流量も同じクエリに相乗りさせる。ハンドルを分ける理由が無い。
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskReadCounter);
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWriteCounter);
            PdhCollectQueryData(_query);   // 1回目は差分の基準になるだけ
            _ready = true;
        }
        catch (DllNotFoundException) { _ready = false; }
        catch (EntryPointNotFoundException) { _ready = false; }
    }

    /// <summary>1秒ごとに1回だけ呼ぶこと。PDH は呼ぶたびに差分を取るため。</summary>
    public (IReadOnlyList<GpuInfo> Gpus, DiskThroughput Disk) Sample()
    {
        if (!_ready || PdhCollectQueryData(_query) != 0)
            return (Array.Empty<GpuInfo>(), new DiskThroughput(0, 0));

        // --- 使用率: (luid, engtype) ごとに合計し、luid ごとに最大を取る ---
        var perEngine = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (instance, value) in ReadArray(_utilCounter))
        {
            string? key = LuidKey(instance);
            if (key is null) continue;
            var engType = EngTypeRe.Match(instance);
            string type = engType.Success ? engType.Groups[1].Value : "other";
            var byType = perEngine.TryGetValue(key, out var d)
                ? d : perEngine[key] = new Dictionary<string, double>();
            byType[type] = byType.GetValueOrDefault(type) + value;
        }

        // --- VRAM: luid ごとに合計 ---
        var vram = new Dictionary<string, double>();
        if (_memCounter != IntPtr.Zero)
        {
            foreach (var (instance, value) in ReadArray(_memCounter))
            {
                string? key = LuidKey(instance);
                if (key is null) continue;
                vram[key] = vram.GetValueOrDefault(key) + value;
            }
        }

        var result = new List<GpuInfo>();
        foreach (string k in perEngine.Keys.Union(vram.Keys))
        {
            double util = perEngine.TryGetValue(k, out var byType) && byType.Count > 0
                ? byType.Values.Max() / 100.0 : 0;
            ulong used = (ulong)Math.Max(0, vram.GetValueOrDefault(k));
            // **LUID が一致したものだけを名乗らせる。**
            // 見つからなければ名無しで出す。間違った型番を出すより、分からないと言う方がまだ良い。
            var id = _identity.GetValueOrDefault(k);
            result.Add(new GpuInfo(k, id?.Name ?? "GPU", Math.Clamp(util, 0, 1), used, id?.DedicatedBytes ?? 0));
        }
        // **名前も VRAM も使用率も無いものは出さない。**
        // 実機に Quest のリンク用仮想ディスプレイが居て、「GPU  0%  0.0/0.0 GB」という
        // 中身の無い行が並んでいた。描いていない物を GPU として数えると、
        // 「うちは GPU 4枚なのか？」という誤解だけが残る。
        // 一度でも動いた（使用率が出た）ものは、名前が無くても残す。
        result.RemoveAll(g => g.Name == "GPU" && g.VramTotalBytes == 0 && g.Utilization <= 0);

        // 積んでいる VRAM が多い順。主に使っているカードが GPU0 になる方が読みやすい。
        result.Sort((a, b) => b.VramTotalBytes.CompareTo(a.VramTotalBytes));

        var disk = new DiskThroughput(ReadSingle(_diskReadCounter), ReadSingle(_diskWriteCounter));
        return (result, disk);
    }

    private double ReadSingle(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return 0;
        if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var value) != 0) return 0;
        return value.CStatus == 0 && !double.IsNaN(value.doubleValue) ? Math.Max(0, value.doubleValue) : 0;
    }

    private IEnumerable<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) yield break;

        uint size = 0, count = 0;
        // 1回目は必要なバッファの大きさを聞くためだけに呼ぶ
        uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) yield break;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, ref count, buffer) != 0) yield break;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                string name = Marshal.PtrToStringUni(item.szName) ?? "";
                if (item.CStatus != 0) continue;
                yield return (name, item.doubleValue);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// 総VRAM。**WMI の Win32_VideoController.AdapterRAM は 4GB で頭打ちになる**ので使わない。
    /// ドライバがレジストリに書く qwMemorySize を読む。
    /// </summary>
    /// <summary>
    /// `..._luid_0x00000000_0x0000E123_...` から鍵を作る。
    /// 桁数の書き方が環境で揺れるので、**数として読んでから揃える。**
    /// </summary>
    private static string? LuidKey(string instance)
    {
        var m = LuidRe.Match(instance);
        if (!m.Success) return null;
        try
        {
            long high = Convert.ToInt64(m.Groups[1].Value, 16);
            ulong low = Convert.ToUInt64(m.Groups[2].Value, 16);
            return GpuIdentity_.KeyOf(high, low);
        }
        catch (OverflowException) { return null; }
        catch (FormatException) { return null; }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    // --- PDH ---
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public uint CStatus;
        private readonly uint _padding;
        public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        private readonly uint _padding;
        public double doubleValue;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
