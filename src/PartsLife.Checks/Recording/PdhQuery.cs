using System.Runtime.InteropServices;

namespace PartsLife.Checks.Recording;

/// <summary>
/// PDH（パフォーマンスカウンタ）の最小の包み。**英語名で登録する**（日本語版 Windows では表示名が翻訳されているため）。
/// 管理者権限は要らない。1 つの照会は 1 本の糸からだけ使う（読み口ごとに専用の糸があるので、そのまま守られる）。
/// </summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private IntPtr _query;
    private IntPtr _buffer;
    private uint _bufferSize;

    public static PdhQuery? TryOpen()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return null;
            return new PdhQuery { _query = q };
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private PdhQuery() { }

    /// <summary>カウンタを足す。この Windows に無ければ <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Add(string englishPath)
    {
        try { return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero; }
        catch (EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    /// <summary>1 回集める。最初の 1 回は差分の基準になるだけ。</summary>
    public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    /// <summary>単一のカウンタの値。読めなければ null。<paramref name="noCap100"/> は 100 を超える値（% Processor Performance）をそのまま読む。</summary>
    public double? Read(IntPtr counter, bool noCap100 = false)
    {
        if (counter == IntPtr.Zero) return null;
        uint fmt = PDH_FMT_DOUBLE | (noCap100 ? PDH_FMT_NOCAP100 : 0u);
        if (PdhGetFormattedCounterValue(counter, fmt, out _, out var v) != 0) return null;
        return v.CStatus is 0 or 1 && double.IsFinite(v.Value) ? v.Value : null;
    }

    /// <summary>
    /// ワイルドカード（`(*)`）のカウンタの全インスタンス。読めなかったインスタンスは含めない。
    /// <paramref name="includeInvalid"/> が真なら、読めなかったインスタンスも NaN で含める（最初の差分の読みで無効になるインスタンスの名前を落とさないため）。
    /// </summary>
    public List<(string Instance, double Value)> ReadArray(IntPtr counter, bool noCap100 = false, bool includeInvalid = false)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint fmt = PDH_FMT_DOUBLE | (noCap100 ? PDH_FMT_NOCAP100 : 0u);

        // インスタンスの数は読むたびに変わりうる（GPU Engine はプロセスごとに出る）。足りなければ広げて数回やり直す
        uint status = 0, count = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = _bufferSize;
            status = PdhGetFormattedCounterArrayW(counter, fmt, ref size, ref count, _buffer);
            if (status != PDH_MORE_DATA) break;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = size + 4096;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
        }
        if (status != 0 || _buffer == IntPtr.Zero) return result;

        var items = (Item*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            bool ok = items[i].CStatus is 0 or 1 && double.IsFinite(items[i].Value);
            if (!ok && !includeInvalid) continue;
            result.Add((Marshal.PtrToStringUni(items[i].Name) ?? "", ok ? items[i].Value : double.NaN));
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W（x64）: 名前へのポインタ 8 ＋ CStatus 4 ＋ 詰め物 4 ＋ double 8。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public IntPtr Name;
        public uint CStatus;
        private readonly uint _pad;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
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
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
