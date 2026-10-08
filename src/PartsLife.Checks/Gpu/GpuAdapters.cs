using System.Runtime.InteropServices;

namespace PartsLife.Checks.Gpu;

/// <summary>DXGI が見ているアダプタ1枚。</summary>
public sealed record GpuAdapter(int DxgiIndex, string Name, uint VendorId, ulong DedicatedBytes,
                                uint LuidLow, int LuidHigh, uint? PciBus, uint? PciDevice, int? NvmlIndex)
{
    public bool IsNvidia => VendorId == 0x10DE;
    /// <summary>温度を見張れるか。**見張れない GPU には負荷をかけない。**</summary>
    public bool CanWatchTemperature => NvmlIndex is not null;
}

/// <summary>
/// GPU の一覧。DXGI のアダプタを、PCI の番地で NVML の GPU と突き合わせる。
///
/// **名前で突き合わせない。** 同じ型番を2枚挿していると取り違える。
/// 取り違えたまま「別の GPU の温度」を見張ると、熱くなっている方を見逃す。
/// 番地が取れなければ、その GPU は「温度を見張れない」扱いにする。
/// </summary>
public static unsafe class GpuAdapters
{
    private const int DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int KMTQAITYPE_ADAPTERADDRESS = 6;

    public static IReadOnlyList<GpuAdapter> Enumerate(Nvml? nvml)
    {
        var list = new List<GpuAdapter>();
        var nvDevices = nvml?.Devices() ?? Array.Empty<NvmlDevice>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IID_IDXGIFactory1
            if (CreateDXGIFactory1(&iid, &factory) < 0) return list;
            var desc = stackalloc byte[312];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                // IDXGIFactory1::EnumAdapters1 = 12
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Slot(factory, 12))(factory, i, &adapter);
                if (hr < 0) break;     // DXGI_ERROR_NOT_FOUND で終わり
                try
                {
                    new Span<byte>(desc, 312).Clear();
                    // IDXGIAdapter1::GetDesc1 = 10
                    if (((delegate* unmanaged<IntPtr, byte*, int>)Com.Slot(adapter, 10))(adapter, desc) < 0) continue;
                    var d = ParseDesc1(new ReadOnlySpan<byte>(desc, 312));
                    if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;    // Basic Render Driver
                    var (bus, dev) = PciAddress(d.LuidLow, d.LuidHigh);
                    int? nv = null;
                    if (bus is { } b && dev is { } dv && d.VendorId == 0x10DE)
                        nv = nvDevices.FirstOrDefault(x => x.Bus == b && x.Device == dv)?.Index;
                    list.Add(new GpuAdapter((int)i, d.Name, d.VendorId, d.Dedicated, d.LuidLow, d.LuidHigh, bus, dev, nv));
                }
                finally { Com.Release(adapter); }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        finally { if (factory != IntPtr.Zero) Com.Release(factory); }
        return Dedupe(list);
    }

    /// <summary>
    /// 同じ GPU の別名を除く。仮想ディスプレイのドライバ（VR のストリーミング等）が入っていると、
    /// DXGI が同じカードを別の LUID でもう1枚見せることがある（実機で RTX 5070 が2枚に見えた）。
    /// そちらは PCI の番地が引けない。**番地の無い物のうち、名前と VRAM が番地のある物と同じなら別名**とみなす。
    /// 同じ型番を本当に2枚挿している場合は、どちらにも番地があるので消えない。
    /// </summary>
    internal static List<GpuAdapter> Dedupe(List<GpuAdapter> list) =>
        list.Where(a => a.PciBus is not null ||
                        !list.Any(b => b.PciBus is not null && b.Name == a.Name && b.DedicatedBytes == a.DedicatedBytes))
            .ToList();

    internal readonly record struct Desc1(string Name, uint VendorId, ulong Dedicated, uint LuidLow, int LuidHigh, uint Flags);

    /// <summary>
    /// DXGI_ADAPTER_DESC1: Description[128 WCHAR] 0, VendorId 256, DeviceId 260, SubSysId 264, Revision 268,
    /// DedicatedVideoMemory 272, DedicatedSystemMemory 280, SharedSystemMemory 288, AdapterLuid 296, Flags 304
    /// </summary>
    internal static Desc1 ParseDesc1(ReadOnlySpan<byte> d)
    {
        var chars = MemoryMarshal.Cast<byte, char>(d[..256]);
        int end = chars.IndexOf('\0');
        string name = new string(end < 0 ? chars : chars[..end]).Trim();
        return new Desc1(name,
            BitConverter.ToUInt32(d[256..]), BitConverter.ToUInt64(d[272..]),
            BitConverter.ToUInt32(d[296..]), BitConverter.ToInt32(d[300..]), BitConverter.ToUInt32(d[304..]));
    }

    /// <summary>LUID から PCI のバス番号・デバイス番号を引く（D3DKMT。管理者権限は要らない）。</summary>
    private static (uint?, uint?) PciAddress(uint luidLow, int luidHigh)
    {
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = luidLow, LuidHigh = luidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return (null, null);
            try
            {
                var addr = new D3DKMT_ADAPTERADDRESS();
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS,
                    pPrivateDriverData = (IntPtr)(&addr), PrivateDriverDataSize = (uint)sizeof(D3DKMT_ADAPTERADDRESS),
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return (null, null);
                // 番地を持たないアダプタ（仮想・ソフトウェア）は 0xFFFFFFFF が返る
                if (addr.BusNumber == uint.MaxValue || addr.DeviceNumber is 0xFFFF or uint.MaxValue) return (null, null);
                return (addr.BusNumber, addr.DeviceNumber);
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch (EntryPointNotFoundException) { return (null, null); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID { public uint LuidLow; public int LuidHigh; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERADDRESS { public uint BusNumber, DeviceNumber, FunctionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);
}

/// <summary>COM の vtable を番号で呼ぶための最小限。</summary>
internal static unsafe class Com
{
    public static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(obj, 2))(obj);
    }
}
