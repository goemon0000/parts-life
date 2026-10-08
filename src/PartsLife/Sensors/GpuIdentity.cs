using System.Runtime.InteropServices;
using PartsLife.Hardware;

namespace PartsLife.Sensors;

/// <summary>
/// GPU の名前と VRAM 総量を、**LUID を鍵にして**引く。
///
/// ■ なぜレジストリを直接読まないか
/// `Control\Class\{4d36e968-...}` には**過去に挿していたカードの残骸が残る。**
/// 実機で確認した例（GPU は3枚しか無いのに6件あった）:
///
///     0000 | NVIDIA GeForce RTX 5070    | 12820938752
///     0001 | AMD Radeon(TM) Graphics    |  4294967296
///     0002 | AMD Radeon(TM) Graphics    |   536870912
///     0003 | NVIDIA GeForce RTX 3080    | 10737418240  ← 今は挿さっていない
///     0004 | Meta Virtual Monitor       |  (無し)
///     0005 | NVIDIA GeForce RTX 3080 Ti | 12884901888  ← 実物
///
/// この一覧を頭から順にカウンタ側と突き合わせると、**1つずれて別のカードの名前と
/// VRAM が付く。** 実際「3080 Ti・12GB」が「3080・10GB」と表示されていた。
/// 番号の並びが一致するという前提が、そもそも成り立たない。
///
/// ■ 代わりに D3DKMT を使う
/// カーネルの表示ドライバに直接聞く。**いま存在するアダプタしか返らず、LUID が付く。**
/// タスクマネージャーが見ているのと同じ経路で、管理者権限も要らない。
///
/// ■ 返す物には GPU でない物も混ざる
/// Microsoft Basic Render Driver（ソフトウェア）と、仮想ディスプレイが見せる同じカードの別名。
/// ここでは PCI の番地とソフトウェアかどうかを添えて全部返し、除くのは GpuRoster（Core）に任せる。
/// </summary>
public static class GpuIdentity_
{
    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int KMTQAITYPE_ADAPTERADDRESS = 6;
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_ADAPTERTYPE = 15;
    /// <summary>D3DKMT_ADAPTERTYPE の SoftwareDevice（3 番目の bit）。</summary>
    private const uint ADAPTERTYPE_SOFTWARE = 0x4;

    /// <summary>PDH のインスタンス名に埋まっている LUID と突き合わせるための鍵。</summary>
    public static string KeyOf(long high, ulong low) => $"{high:X}:{low:X}";

    public static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var list = new List<GpuAdapterInfo>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var probe = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            if (D3DKMTEnumAdapters2(ref probe) != 0 || probe.NumAdapters == 0) return list;

            int stride = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            buffer = Marshal.AllocHGlobal(stride * (int)probe.NumAdapters);
            var call = new D3DKMT_ENUMADAPTERS2 { NumAdapters = probe.NumAdapters, pAdapters = buffer };
            if (D3DKMTEnumAdapters2(ref call) != 0) return list;

            for (int i = 0; i < call.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * stride);
                try
                {
                    string name = QueryName(info.hAdapter);
                    ulong dedicated = QueryDedicated(info.hAdapter);
                    list.Add(new GpuAdapterInfo(
                        KeyOf(info.AdapterLuid.HighPart, info.AdapterLuid.LowPart), Shorten(name), dedicated,
                        QueryPciBus(info.hAdapter), IsSoftware(info.hAdapter)));
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        return list;
    }

    /// <summary>
    /// 名前を詰める。**表示幅が足りないと、末尾が黙って切れて別の型番になる。**
    /// 「RTX 3080 Ti」が「RTX 3080」と読めてしまうのが一番まずい。
    /// </summary>
    private static string Shorten(string name) => name
        .Replace("NVIDIA GeForce", "GeForce")
        .Replace("AMD Radeon(TM)", "Radeon")
        .Replace("(R)", "").Replace("(TM)", "")
        .Trim();

    private static string QueryName(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTERREGISTRYINFO>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERREGISTRYINFO,
                pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return "";
            return Marshal.PtrToStructure<D3DKMT_ADAPTERREGISTRYINFO>(p).AdapterString ?? "";
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>PCI のバス番号。番地を持たないアダプタ（仮想・ソフトウェア）は null。</summary>
    private static uint? QueryPciBus(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTERADDRESS>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS,
                pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
            var addr = Marshal.PtrToStructure<D3DKMT_ADAPTERADDRESS>(p);
            // 番地を持たないアダプタには 0xFFFFFFFF が返る（点検の GpuAdapters と同じ判定）
            if (addr.BusNumber == uint.MaxValue || addr.DeviceNumber is 0xFFFF or uint.MaxValue) return null;
            return addr.BusNumber;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>ソフトウェアのアダプタ（Microsoft Basic Render Driver）か。聞けなければ偽。</summary>
    private static bool IsSoftware(uint hAdapter)
    {
        IntPtr p = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(p, 0);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERTYPE,
                pPrivateDriverData = p, PrivateDriverDataSize = sizeof(uint),
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            return ((uint)Marshal.ReadInt32(p) & ADAPTERTYPE_SOFTWARE) != 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static ulong QueryDedicated(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_SEGMENTSIZEINFO>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_GETSEGMENTSIZE,
                pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return 0;
            return Marshal.PtrToStructure<D3DKMT_SEGMENTSIZEINFO>(p).DedicatedVideoMemorySize;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // --- P/Invoke ---------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public uint bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERADDRESS { public uint BusNumber, DeviceNumber, FunctionNumber; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_SEGMENTSIZEINFO
    {
        public ulong DedicatedVideoMemorySize;
        public ulong DedicatedSystemMemorySize;
        public ulong SharedSystemMemorySize;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
}
