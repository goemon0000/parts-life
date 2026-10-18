using System.Runtime.InteropServices;
using PartsLife.Checks.Gpu;

namespace PartsLife.Checks.Recording;

/// <summary>
/// AMD の GPU の温度・ホットスポット・クロック・電力・ファンを、**AMD のドライバが System32 に置く amdadlx64.dll（ADLX）**から読む。
/// 新しい DLL は同梱しない・管理者は要らない・読み出しだけ（性能の監視の関数だけ。チューニングや設定の関数は呼ばない）。
///
/// ■ 呼ぶ物（ADLX SDK の C の vtable の並びどおり。AMD の公開ヘッダと突き合わせた）
/// - 書き出された関数: ADLXInitialize / ADLXTerminate / ADLXQueryFullVersion（無ければ版 1.0.0.0 で初期化する）
/// - IADLXSystem: 1 GetGPUs・9 GetPerformanceMonitoringServices
/// - IADLXGPUList: 1 Release・3 Size・11 At_GPUList　IADLXGPU: 1 Release・7 Name
/// - IADLXPerformanceMonitoringServices: 18 GetCurrentGPUMetrics
/// - IADLXGPUMetrics: 1 Release・5 GPUClockSpeed・6 GPUVRAMClockSpeed・7 GPUTemperature・8 GPUHotspotTemperature・9 GPUPower・10 GPUTotalBoardPower・11 GPUFanSpeed
///
/// ■ 使用率とメモリは読まない（PDH が受け持つ。タスクマネージャーと同じ物差しで、NVIDIA 以外の GPU を揃えるため）
/// ■ どの ADLX の GPU がどの記録の `gpuN` かは、**名前で突き合わせる**。同じ名前の GPU が複数あって見分けられないときは、
///   取り違えるより読まない（<see cref="Reasons.AdlxAmbiguous"/>）
/// ■ 初期化・読み取りは読み口の専用の糸で行う（固まっても記録の糸は止まらない）。終了では ADLXTerminate まで呼び、
///   DLL は解放しない（ADLX が裏で糸を持つことがあるため。プロセスの終わりまで読み込んだままにする）
/// </summary>
internal sealed unsafe class AdlxGpuSource : ISensorSource
{
    private const string DllName = "amdadlx64.dll";
    private static string DllPath => Path.Combine(Environment.SystemDirectory, DllName);

    // ADLX_RESULT。0 = OK、1 = 有効済み、2 = 初期化済み、12 = 非対応
    private static bool Succeeded(int rc) => rc is 0 or 1 or 2;
    private const int NotSupported = 12;

    private readonly List<GpuSlot> _slots;
    private IntPtr _system, _perf;
    private delegate* unmanaged<int> _terminate;
    private readonly Dictionary<int, IntPtr> _gpuOfSlot = new();
    private readonly HashSet<string> _available = new(StringComparer.Ordinal);
    private bool _initialized, _disposed;

    private AdlxGpuSource(IReadOnlyList<GpuSlot> slots) { _slots = slots.ToList(); }

    /// <summary>ドライバの DLL が無ければ null（その GPU は「ADLX から読めない」と答える）。読み込みと初期化は <see cref="Detect"/> で。</summary>
    public static AdlxGpuSource? TryCreate(IReadOnlyList<GpuSlot> slots) => File.Exists(DllPath) ? new AdlxGpuSource(slots) : null;

    public string Id => "adlx";

    private static readonly (string Metric, string Unit)[] Fields =
    {
        (MetricKeys.Temp, "°C"), (MetricKeys.HotspotTemp, "°C"), (MetricKeys.Clock, "MHz"), (MetricKeys.MemClock, "MHz"),
        (MetricKeys.Power, "W"), (MetricKeys.FanRpm, "rpm"),
    };

    public IReadOnlyList<MetricStatus> Detect()
    {
        var list = new List<MetricStatus>();
        string failure = Reasons.NoAdlx;
        try
        {
            if (Open(out var gpus)) Map(gpus, out failure);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            failure = Reasons.NoAdlx;
        }

        foreach (var slot in _slots)
        {
            if (!_gpuOfSlot.TryGetValue(slot.Index, out var gpu))
            {
                foreach (var (metric, unit) in Fields)
                    list.Add(new MetricStatus(MetricKeys.Gpu(slot.Index, metric), unit, "adlx", Availability.Unavailable, failure));
                continue;
            }
            var values = ReadMetrics(gpu, out int rcAll);
            foreach (var (metric, unit) in Fields)
            {
                string key = MetricKeys.Gpu(slot.Index, metric);
                if (values is not null && values.TryGetValue(metric, out var v) && v.Status == 0)
                {
                    _available.Add(key);
                    list.Add(new MetricStatus(key, unit, "adlx:" + v.Source, Availability.Available));
                }
                else
                {
                    int rc = values is not null && values.TryGetValue(metric, out var v2) ? v2.Status : rcAll;
                    list.Add(new MetricStatus(key, unit, "adlx", Availability.Unavailable, rc == NotSupported ? Reasons.NotSupported : Reasons.AdlxNoMetric));
                }
            }
        }
        return list;
    }

    public void Prime() { }

    public void Read(SampleRow row)
    {
        foreach (var slot in _slots)
        {
            if (!_gpuOfSlot.TryGetValue(slot.Index, out var gpu)) continue;
            var values = ReadMetrics(gpu, out _);
            if (values is null) continue;
            foreach (var (metric, _) in Fields)
            {
                string key = MetricKeys.Gpu(slot.Index, metric);
                if (_available.Contains(key) && values.TryGetValue(metric, out var v) && v.Status == 0) row.Set(key, v.Number);
            }
        }
    }

    // =====================================================================
    // ADLX を開く
    // =====================================================================

    private bool Open(out List<(IntPtr Gpu, string Name)> gpus)
    {
        gpus = new List<(IntPtr, string)>();
        if (!NativeLibrary.TryLoad(DllPath, out var lib)) return false;
        if (!NativeLibrary.TryGetExport(lib, "ADLXInitialize", out var initPtr)) return false;
        _terminate = NativeLibrary.TryGetExport(lib, "ADLXTerminate", out var termPtr) ? (delegate* unmanaged<int>)termPtr : null;

        // ドライバが話せる版そのものを名乗る（ドライバより新しい版を名乗ると断られるため）。聞けなければ 1.0.0.0
        ulong version = 1UL << 48;
        if (NativeLibrary.TryGetExport(lib, "ADLXQueryFullVersion", out var qv))
        {
            ulong v = 0;
            if (((delegate* unmanaged<ulong*, int>)qv)(&v) == 0 && v != 0) version = v;
        }

        IntPtr system = IntPtr.Zero;
        int rc = ((delegate* unmanaged<ulong, IntPtr*, int>)initPtr)(version, &system);
        if (!Succeeded(rc) || system == IntPtr.Zero) return false;
        _system = system;
        _initialized = true;

        IntPtr list = IntPtr.Zero;
        if (!Succeeded(((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Slot(system, 1))(system, &list)) || list == IntPtr.Zero) return true;
        try
        {
            uint count = ((delegate* unmanaged<IntPtr, uint>)Com.Slot(list, 3))(list);
            for (uint i = 0; i < count && i < 16; i++)
            {
                IntPtr gpu = IntPtr.Zero;
                if (!Succeeded(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Slot(list, 11))(list, i, &gpu)) || gpu == IntPtr.Zero) continue;
                byte* name = null;
                string n = Succeeded(((delegate* unmanaged<IntPtr, byte**, int>)Com.Slot(gpu, 7))(gpu, &name)) && name != null
                    ? Marshal.PtrToStringUTF8((IntPtr)name) ?? "" : "";
                gpus.Add((gpu, n));
            }
        }
        finally { Release(list); }

        IntPtr perf = IntPtr.Zero;
        if (Succeeded(((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Slot(system, 9))(system, &perf))) _perf = perf;
        return true;
    }

    /// <summary>ADLX の GPU を記録の GPU に名前で結ぶ。1 対 1 で決まらなければ結ばない（取り違えない）。</summary>
    private void Map(List<(IntPtr Gpu, string Name)> gpus, out string failure)
    {
        failure = Reasons.AdlxNoMetric;
        if (_perf == IntPtr.Zero) { failure = Reasons.NoAdlx; foreach (var g in gpus) Release(g.Gpu); return; }

        var pairs = Pair(_slots.Select(s => (s.Index, s.Adapter.Name)).ToList(), gpus.Select(g => g.Name).ToList(), out bool ambiguous);
        if (ambiguous) failure = Reasons.AdlxAmbiguous;
        var used = new HashSet<int>();
        foreach (var (slotIndex, adlxIndex) in pairs)
        {
            _gpuOfSlot[slotIndex] = gpus[adlxIndex].Gpu;
            used.Add(adlxIndex);
        }
        for (int i = 0; i < gpus.Count; i++) if (!used.Contains(i)) Release(gpus[i].Gpu);
    }

    /// <summary>
    /// 記録の GPU（番号と名前）と ADLX の GPU（名前）を結ぶ。名前を整えて（大小・記号・先頭の AMD を無視）同じ物が
    /// 両側に 1 つずつなら結ぶ。名前が合わなくても、AMD の GPU が両側に 1 枚ずつなら結ぶ。
    /// **同じ名前の GPU が複数あって見分けられなければ、結ばない**（<paramref name="ambiguous"/>）。
    /// </summary>
    internal static Dictionary<int, int> Pair(IReadOnlyList<(int Index, string Name)> slots, IReadOnlyList<string> adlxNames, out bool ambiguous)
    {
        ambiguous = false;
        var result = new Dictionary<int, int>();
        foreach (var (index, name) in slots)
        {
            string n = Norm(name);
            var candidates = Enumerable.Range(0, adlxNames.Count).Where(i => Norm(adlxNames[i]) == n).ToList();
            if (candidates.Count == 0 && slots.Count == 1 && adlxNames.Count == 1) candidates.Add(0);
            int sameNameSlots = slots.Count(s => Norm(s.Name) == n);
            if (candidates.Count == 1 && sameNameSlots == 1) result[index] = candidates[0];
            else if (candidates.Count > 1 || (candidates.Count == 1 && sameNameSlots > 1)) ambiguous = true;
        }
        return result;
    }

    internal static string Norm(string s)
    {
        // 商標の印（(TM) (R)）は DXGI の名前にだけ付くことがあるので落とす
        s = s.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase);
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        string t = new(chars);
        return t.StartsWith("amd", StringComparison.Ordinal) ? t[3..] : t;
    }

    // =====================================================================
    // 読む
    // =====================================================================

    private readonly record struct Reading(double Number, int Status, string Source);

    /// <summary>1 枚ぶん。読めた項目は Status 0。取れなかった理由（ADLX_RESULT）は項目ごと。</summary>
    private Dictionary<string, Reading>? ReadMetrics(IntPtr gpu, out int rcAll)
    {
        rcAll = 3;
        if (_perf == IntPtr.Zero) return null;
        IntPtr m = IntPtr.Zero;
        rcAll = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Com.Slot(_perf, 18))(_perf, gpu, &m);
        if (!Succeeded(rcAll) || m == IntPtr.Zero) return null;
        try
        {
            var d = new Dictionary<string, Reading>(StringComparer.Ordinal);
            d[MetricKeys.Temp] = Dbl(m, 7, "GPUTemperature");
            d[MetricKeys.HotspotTemp] = Dbl(m, 8, "GPUHotspotTemperature");
            d[MetricKeys.Clock] = Int(m, 5, "GPUClockSpeed");
            d[MetricKeys.MemClock] = Int(m, 6, "GPUVRAMClockSpeed");
            // 電力: ボード全体の値があればそれ（NVIDIA の値と同じ範囲）。無ければ GPU チップの値
            var board = Dbl(m, 10, "GPUTotalBoardPower");
            d[MetricKeys.Power] = board.Status == 0 ? board : Dbl(m, 9, "GPUPower");
            d[MetricKeys.FanRpm] = Int(m, 11, "GPUFanSpeed");
            return d;
        }
        finally { Release(m); }
    }

    private static Reading Dbl(IntPtr obj, int slot, string name)
    {
        double v = 0;
        int rc = ((delegate* unmanaged<IntPtr, double*, int>)Com.Slot(obj, slot))(obj, &v);
        return Succeeded(rc) && double.IsFinite(v) ? new Reading(v, 0, name) : new Reading(double.NaN, rc == 0 ? 3 : rc, name);
    }

    private static Reading Int(IntPtr obj, int slot, string name)
    {
        int v = 0;
        int rc = ((delegate* unmanaged<IntPtr, int*, int>)Com.Slot(obj, slot))(obj, &v);
        return Succeeded(rc) ? new Reading(v, 0, name) : new Reading(double.NaN, rc, name);
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, int>)Com.Slot(obj, 1))(obj);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            foreach (var g in _gpuOfSlot.Values) Release(g);
            _gpuOfSlot.Clear();
            Release(_perf);
            _perf = IntPtr.Zero;
            if (_initialized && _terminate != null) _terminate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _initialized = false;
    }
}
