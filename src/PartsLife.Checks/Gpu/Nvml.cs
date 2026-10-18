using System.Runtime.InteropServices;
using System.Text;

namespace PartsLife.Checks.Gpu;

/// <summary>NVML が見ている GPU 1枚。PCI の番地で DXGI のアダプタと突き合わせる。</summary>
public sealed record NvmlDevice(int Index, string Name, uint Domain, uint Bus, uint Device);

/// <summary>
/// NVML で読んだ値 1 つ。<see cref="Status"/> が 0 なら読めた。-1 はこのドライバにその関数が無い、
/// それ以外は NVML の戻り値（3 = このカードは非対応、4 = 権限が足りない …）。
/// </summary>
public readonly record struct NvmlValue(double Value, int Status)
{
    public bool Ok => Status == 0;
    public static NvmlValue Missing => new(double.NaN, -1);
    public static NvmlValue Failed(int status) => new(double.NaN, status);
}

/// <summary>GPU 1枚の「いま」。電力は W、メモリは byte、<see cref="ThrottleMask"/> は nvmlClocksEventReasons の bit。</summary>
public sealed record NvmlSample(NvmlValue TemperatureC, NvmlValue GraphicsMHz, NvmlValue MemoryMHz, NvmlValue PowerW,
                                NvmlValue PowerLimitW, NvmlValue FanPercent, NvmlValue UtilizationPercent,
                                NvmlValue MemoryUsedBytes, NvmlValue ThrottleMask);

/// <summary>
/// NVIDIA のドライバに付いてくる nvml.dll から温度を読む。**管理者権限は要らない。**
///
/// ■ 新しい依存は増やさない
/// nvml.dll はドライバと一緒に System32 に入っている物を、その場所を名指しで読み込む
/// （検索パスに任せると、同じ名前の別物を拾う余地がある）。無ければ「温度は読めない」と言うだけ。
/// ■ 読むだけ
/// 使う関数は初期化・数・名前・PCI の番地・温度・終了の6つに、テストの記録（Record）が読む
/// クロック・電力・電力の上限・ファン・使用率・メモリ・制限の理由の7つを足した13個。
/// **全部「読み出し」で、設定を変える関数（クロック固定・電力上限の変更・ファン・OC）は呼ばない。**
/// 足した7つは古いドライバに無いことがあるので、無ければその項目だけ「読めない」にする（全体は落ちない）。
/// ■ 中に糸がいる間は解放しない
/// 読み取りが固まって戻らない糸がいるときに nvmlShutdown / FreeLibrary を呼ぶと、その糸は
/// 解放された場所で動き出す。<see cref="Dispose"/> は中に誰かいれば何も解放せずに手放す（漏らす）。
/// </summary>
public sealed unsafe class Nvml : IDisposable
{
    private readonly IntPtr _lib;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<uint*, int> _count;
    private readonly delegate* unmanaged<uint, IntPtr*, int> _handle;
    private readonly delegate* unmanaged<IntPtr, byte*, uint, int> _name;
    private readonly delegate* unmanaged<IntPtr, byte*, int> _pci;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _temp;
    // --- テストの記録が読む物（無いドライバもあるので、無ければ null。読み出しだけ） ---
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _clock;       // nvmlDeviceGetClockInfo
    private readonly delegate* unmanaged<IntPtr, uint*, int> _power;            // nvmlDeviceGetPowerUsage（mW）
    private readonly delegate* unmanaged<IntPtr, uint*, int> _powerLimit;       // nvmlDeviceGetEnforcedPowerLimit（mW）
    private readonly delegate* unmanaged<IntPtr, uint*, int> _fan;              // nvmlDeviceGetFanSpeed（%）
    private readonly delegate* unmanaged<IntPtr, uint*, int> _util;             // nvmlDeviceGetUtilizationRates（uint 2 つ: gpu, memory）
    private readonly delegate* unmanaged<IntPtr, ulong*, int> _memInfo;         // nvmlDeviceGetMemoryInfo（ulong 3 つ: total, free, used）
    private readonly delegate* unmanaged<IntPtr, ulong*, int> _throttle;        // nvmlDeviceGetCurrentClocksEventReasons / ...ThrottleReasons
    private bool _open;
    private readonly InsideGuard _guard = new();

    /// <summary>閉じるときに読み取りの途中の糸がいたので、解放せずに手放した。</summary>
    public bool Leaked { get; private set; }

    private Nvml(IntPtr lib)
    {
        _lib = lib;
        _shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlShutdown");
        _count = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
        _handle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
        _name = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
        _pci = (delegate* unmanaged<IntPtr, byte*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPciInfo_v3");
        _temp = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature");
        _clock = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional(lib, "nvmlDeviceGetClockInfo");
        _power = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetEnforcedPowerLimit");
        _fan = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetFanSpeed");
        _util = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetUtilizationRates");
        _memInfo = (delegate* unmanaged<IntPtr, ulong*, int>)Optional(lib, "nvmlDeviceGetMemoryInfo");
        // 新しいドライバは「Clocks Event Reasons」、古いドライバは「Clocks Throttle Reasons」の名前で出している（同じ物）
        void* reasons = Optional(lib, "nvmlDeviceGetCurrentClocksEventReasons");
        if (reasons == null) reasons = Optional(lib, "nvmlDeviceGetCurrentClocksThrottleReasons");
        _throttle = (delegate* unmanaged<IntPtr, ulong*, int>)reasons;
        var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
        _open = init() == 0;
    }

    /// <summary>読み込めなければ null（NVIDIA の GPU が無い、ドライバが古い等）。</summary>
    public static Nvml? TryOpen()
    {
        foreach (var path in Candidates())
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) continue;
            try
            {
                var n = new Nvml(lib);
                if (n._open) return n;
                n.Dispose();
            }
            catch (EntryPointNotFoundException) { NativeLibrary.Free(lib); }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                  "NVIDIA Corporation", "NVSMI", "nvml.dll");
    }

    public IReadOnlyList<NvmlDevice> Devices()
    {
        var list = new List<NvmlDevice>();
        if (!_open || !_guard.TryEnter()) return list;
        try { DevicesCore(list); }
        finally { _guard.Exit(); }
        return list;
    }

    private void DevicesCore(List<NvmlDevice> list)
    {
        uint n = 0;
        if (_count(&n) != 0) return;
        var name = stackalloc byte[96];
        var pci = stackalloc byte[128];
        for (uint i = 0; i < n; i++)
        {
            IntPtr h;
            if (_handle(i, &h) != 0) continue;
            string nm = _name(h, name, 96) == 0 ? Marshal.PtrToStringUTF8((IntPtr)name) ?? "" : "";
            // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId, busId[32]
            new Span<byte>(pci, 128).Clear();
            if (_pci(h, pci) != 0) continue;
            uint domain = *(uint*)(pci + 16), bus = *(uint*)(pci + 20), dev = *(uint*)(pci + 24);
            list.Add(new NvmlDevice((int)i, nm, domain, bus, dev));
        }
    }

    /// <summary>GPU の温度（℃）。読めなければ null。</summary>
    public int? Temperature(int index)
    {
        if (!_open || !_guard.TryEnter()) return null;
        try
        {
            IntPtr h;
            if (_handle((uint)index, &h) != 0) return null;
            uint t;
            return _temp(h, 0 /*NVML_TEMPERATURE_GPU*/, &t) == 0 ? (int)t : null;
        }
        finally { _guard.Exit(); }
    }

    private static void* Optional(IntPtr lib, string name) =>
        NativeLibrary.TryGetExport(lib, name, out var p) ? (void*)p : null;

    /// <summary>
    /// GPU 1枚ぶんの「いま」を読む（テストの記録用）。項目ごとに独立していて、1つ読めなくても他は返る。
    /// 読めない理由は <see cref="NvmlValue.Status"/>（-1 = このドライバにその関数が無い、3 = このカードは非対応、4 = 権限）。
    /// 閉じた後・GPU が引けないときは null。**読み出しだけ。**
    /// </summary>
    public NvmlSample? Sample(int index)
    {
        if (!_open || !_guard.TryEnter()) return null;
        try
        {
            IntPtr h;
            if (_handle((uint)index, &h) != 0) return null;
            uint u;
            uint* util2 = stackalloc uint[2];
            ulong* mem3 = stackalloc ulong[3];
            ulong mask;

            NvmlValue temp, gfx, memClk, power, limit, fan, usage, used, thr;

            if (_temp == null) temp = NvmlValue.Missing;
            else { int rc = _temp(h, 0 /*NVML_TEMPERATURE_GPU*/, &u); temp = rc == 0 ? new NvmlValue(u, 0) : NvmlValue.Failed(rc); }
            if (_clock == null) gfx = memClk = NvmlValue.Missing;
            else
            {
                int rc = _clock(h, 0 /*NVML_CLOCK_GRAPHICS*/, &u); gfx = rc == 0 ? new NvmlValue(u, 0) : NvmlValue.Failed(rc);
                rc = _clock(h, 2 /*NVML_CLOCK_MEM*/, &u); memClk = rc == 0 ? new NvmlValue(u, 0) : NvmlValue.Failed(rc);
            }
            if (_power == null) power = NvmlValue.Missing;
            else { int rc = _power(h, &u); power = rc == 0 ? new NvmlValue(u / 1000.0, 0) : NvmlValue.Failed(rc); }       // mW → W
            if (_powerLimit == null) limit = NvmlValue.Missing;
            else { int rc = _powerLimit(h, &u); limit = rc == 0 ? new NvmlValue(u / 1000.0, 0) : NvmlValue.Failed(rc); }
            if (_fan == null) fan = NvmlValue.Missing;
            else { int rc = _fan(h, &u); fan = rc == 0 ? new NvmlValue(u, 0) : NvmlValue.Failed(rc); }
            if (_util == null) usage = NvmlValue.Missing;
            else { int rc = _util(h, util2); usage = rc == 0 ? new NvmlValue(util2[0], 0) : NvmlValue.Failed(rc); }
            if (_memInfo == null) used = NvmlValue.Missing;
            else { int rc = _memInfo(h, mem3); used = rc == 0 ? new NvmlValue(mem3[2], 0) : NvmlValue.Failed(rc); }
            if (_throttle == null) thr = NvmlValue.Missing;
            else { int rc = _throttle(h, &mask); thr = rc == 0 ? new NvmlValue(mask, 0) : NvmlValue.Failed(rc); }
            return new NvmlSample(temp, gfx, memClk, power, limit, fan, usage, used, thr);
        }
        finally { _guard.Exit(); }
    }

    /// <summary>
    /// 終了して DLL を解放する。**読み取りの途中の糸がいれば、どちらもしない**（漏らす方が安全）。
    /// 待たない（画面の糸から呼ばれる）。
    /// </summary>
    public void Dispose()
    {
        if (_guard.IsClosed) return;
        if (!_guard.TryClose())
        {
            Leaked = true;
            System.Diagnostics.Trace.TraceWarning(
                "PartsLife: an NVML call is still running; nvml.dll is left loaded (not shut down, not freed).");
            return;
        }
        if (_open) { _shutdown(); _open = false; }
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }
}
