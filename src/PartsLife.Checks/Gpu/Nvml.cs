using System.Runtime.InteropServices;
using System.Text;

namespace PartsLife.Checks.Gpu;

/// <summary>NVML が見ている GPU 1枚。PCI の番地で DXGI のアダプタと突き合わせる。</summary>
public sealed record NvmlDevice(int Index, string Name, uint Domain, uint Bus, uint Device);

/// <summary>
/// NVIDIA のドライバに付いてくる nvml.dll から温度を読む。**管理者権限は要らない。**
///
/// ■ 新しい依存は増やさない
/// nvml.dll はドライバと一緒に System32 に入っている物を、その場所を名指しで読み込む
/// （検索パスに任せると、同じ名前の別物を拾う余地がある）。無ければ「温度は読めない」と言うだけ。
/// ■ 読むだけ
/// 使う関数は初期化・数・名前・PCI の番地・温度・終了の6つだけ。設定を変える関数は呼ばない。
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
    private bool _open;

    private Nvml(IntPtr lib)
    {
        _lib = lib;
        _shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlShutdown");
        _count = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
        _handle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
        _name = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
        _pci = (delegate* unmanaged<IntPtr, byte*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPciInfo_v3");
        _temp = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature");
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
        if (!_open) return list;
        uint n = 0;
        if (_count(&n) != 0) return list;
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
        return list;
    }

    /// <summary>GPU の温度（℃）。読めなければ null。</summary>
    public int? Temperature(int index)
    {
        if (!_open) return null;
        IntPtr h;
        if (_handle((uint)index, &h) != 0) return null;
        uint t;
        return _temp(h, 0 /*NVML_TEMPERATURE_GPU*/, &t) == 0 ? (int)t : null;
    }

    public void Dispose()
    {
        if (_open) { _shutdown(); _open = false; }
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }
}
