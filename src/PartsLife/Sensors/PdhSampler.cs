using System.Runtime.InteropServices;
using PartsLife.Hardware;

namespace PartsLife.Sensors;

public sealed record GpuInfo(string Key, string Name, double Utilization, ulong VramUsedBytes, ulong VramTotalBytes);

/// <summary>
/// ディスクの読み書き速度（バイト/秒）。
/// <paramref name="PerDisk"/> は物理ディスク番号ごとの合計。
/// **台ごとに分けないと、SSD が働いたぶんで HDDくんが育ってしまう。**
/// </summary>
public sealed record DiskThroughput(
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    IReadOnlyDictionary<uint, double> PerDisk)
{
    public static readonly DiskThroughput Zero =
        new(0, 0, new Dictionary<uint, double>());

    /// <summary>指定した物理ディスクたちの合計（バイト/秒）。</summary>
    public double For(IEnumerable<uint> numbers)
    {
        double sum = 0;
        foreach (var n in numbers) sum += PerDisk.GetValueOrDefault(n);
        return sum;
    }
}

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
    private IntPtr _diskPerCounter;
    private bool _ready;

    /// <summary>
    /// インスタンス名 → (LUID の鍵, エンジンの種類)。**同じ名前を毎回読み直さない。**
    /// 名前はプロセスごとに出るので数百〜千を超え、計測のたびに全部を読み直すと、そのぶん常駐の割り当てが増える。
    /// 名前にはプロセス番号が入り、プロセスが入れ替わると増え続けるので、上限を超えたら一度空にする。
    /// </summary>
    private readonly Dictionary<string, (string? Key, string Type)> _names = new(StringComparer.Ordinal);
    private const int MaxCachedNames = 2048;

    /// <summary>
    /// 盤に立てる GPU（名前・総VRAM・並び）。**D3DKMT から拾い、その秒のカウンタでは決めない。**
    /// レジストリの並び順で突き合わせると、外したカードの残骸に当たって別物になる
    /// （経緯は GpuIdentity.cs）。カウンタに出た LUID を毎秒そのまま並べると、
    /// Basic Render や仮想ディスプレイの別名が動いた秒だけ GPU が1枚増える（経緯は Core の GpuRoster）。
    /// </summary>
    private List<GpuAdapterInfo> _real = new();
    /// <summary>D3DKMT が返した LUID 全部（除いた物も含む）。知らない LUID がカウンタに出たら引き直す。</summary>
    private HashSet<string> _known = new();
    private bool _enumerated;
    private long _lastEnumerate;

    /// <summary>
    /// アダプタを引き直す。**ドライバを入れ直すと LUID が変わる**ので、知らない LUID がカウンタに出てきたら
    /// 引き直す（ただし 1 分に 1 回まで。除いた物はもう知っているので、毎秒は走らない）。
    /// </summary>
    private void Enumerate()
    {
        var all = GpuIdentity_.Enumerate();
        _enumerated = all.Count > 0;
        _real = GpuRoster.Real(all);
        _known = all.Select(a => a.LuidKey).ToHashSet();
        _lastEnumerate = Environment.TickCount64;
    }

    public PdhSampler()
    {
        Enumerate();
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _utilCounter) != 0) return;
            // VRAM のカウンタは環境によって無い。失敗しても使用率だけで続ける。
            PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memCounter);
            // ディスクの流量も同じクエリに相乗りさせる。ハンドルを分ける理由が無い。
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskReadCounter);
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWriteCounter);
            // 台ごとの流量。インスタンス名は "0 C:" のように**先頭が物理ディスク番号**
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Bytes/sec", IntPtr.Zero, out _diskPerCounter);
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
            return (Array.Empty<GpuInfo>(), DiskThroughput.Zero);

        // --- 使用率: (luid, engtype) ごとに合計し、luid ごとに最大を取る ---
        var perEngine = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (instance, value) in ReadArray(_utilCounter))
        {
            var (key, type) = Parse(instance);
            if (key is null) continue;
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
                var (key, _) = Parse(instance);
                if (key is null) continue;
                vram[key] = vram.GetValueOrDefault(key) + value;
            }
        }

        var util = new Dictionary<string, double>();
        foreach (var (k, byType) in perEngine)
            if (byType.Count > 0) util[k] = byType.Values.Max() / 100.0;

        if (Environment.TickCount64 - _lastEnumerate >= 60_000
            && util.Keys.Concat(vram.Keys).Any(k => !_known.Contains(k)))
            Enumerate();

        // **顔ぶれはアダプタの一覧で決める。** 名前・VRAM の無い行や Basic Render は、そこで既に除いてある。
        // 並びは VRAM の多い順（主に使っているカードが GPU0 になる方が読みやすい）、同じなら PCI のバス番号順
        var result = new List<GpuInfo>();
        foreach (var g in GpuRoster.Compose(_real, _enumerated, util, vram))
            result.Add(new GpuInfo(g.Key, g.Name, g.Utilization, g.VramUsedBytes, g.VramTotalBytes));

        var perDisk = new Dictionary<uint, double>();
        foreach (var (instance, value) in ReadArray(_diskPerCounter))
        {
            // "0 C:" / "1 D: E:" / "_Total" の形。先頭の数だけ見る
            int sp = instance.IndexOf(' ');
            string head = sp > 0 ? instance[..sp] : instance;
            if (!uint.TryParse(head, out uint n)) continue;     // _Total はここで落ちる
            perDisk[n] = perDisk.GetValueOrDefault(n) + Math.Max(0, value);
        }

        var disk = new DiskThroughput(
            ReadSingle(_diskReadCounter), ReadSingle(_diskWriteCounter), perDisk);
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
    /// `..._luid_0x00000000_0x0000E123_..._engtype_3D` から鍵と種類を読む（読み方は Core の GpuCounterName）。
    /// 桁数の書き方が環境で揺れるので、**数として読んでから揃える。**
    /// </summary>
    private (string? Key, string Type) Parse(string instance)
    {
        if (_names.TryGetValue(instance, out var hit)) return hit;
        if (_names.Count >= MaxCachedNames) _names.Clear();
        var parsed = (GpuCounterName.LuidKey(instance), GpuCounterName.EngineType(instance));
        _names[instance] = parsed;
        return parsed;
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
