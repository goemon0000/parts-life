using System.Diagnostics;
using System.Management;
using PartsLife.Checks.Gpu;

namespace PartsLife.Checks.Build;

/// <summary>
/// 部品を読む先。分類ごとに1つ。**試験では差し替える**（読めない・例外を投げる先を渡して、空の分類になるのを確かめる）。
/// 既定は <see cref="Windows"/>（WMI と DXGI。管理者は要らない）。
/// </summary>
public sealed class BuildSources
{
    public Func<IEnumerable<string>> Cpus { get; init; } = () => Array.Empty<string>();
    public Func<(string? Manufacturer, string? Product)> Board { get; init; } = () => (null, null);
    public Func<IEnumerable<RawMemory>> Memory { get; init; } = () => Array.Empty<RawMemory>();
    public Func<IEnumerable<RawGpu>> Gpus { get; init; } = () => Array.Empty<RawGpu>();
    public Func<IEnumerable<RawDisk>> Disks { get; init; } = () => Array.Empty<RawDisk>();
    public Func<IEnumerable<RawMonitor>> Monitors { get; init; } = () => Array.Empty<RawMonitor>();
    public Func<IEnumerable<RawOptical>> Opticals { get; init; } = () => Array.Empty<RawOptical>();

    /// <summary>この機械の WMI・DXGI。</summary>
    public static BuildSources Windows => new()
    {
        Cpus = WindowsReaders.Cpus,
        Board = WindowsReaders.Board,
        Memory = WindowsReaders.Memory,
        Gpus = WindowsReaders.Gpus,
        Disks = WindowsReaders.Disks,
        Monitors = WindowsReaders.Monitors,
        Opticals = WindowsReaders.Opticals,
    };
}

/// <summary>
/// この機械の部品を集める。**読めなかった分類は黙って空にする**（例外を外に出さない）。
/// 何が読めなかったかは Trace に残す（点検の他の所と同じ）。WMI は遅いので UI スレッドで呼ばないこと。
/// </summary>
public static class BuildCollector
{
    public static BuildConfig Collect(BuildSources? sources = null, CancellationToken ct = default)
    {
        var s = sources ?? BuildSources.Windows;
        var c = new BuildConfig();

        void Try(string what, Action read)
        {
            ct.ThrowIfCancellationRequested();
            try { read(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceWarning($"PartsLife: build code: could not read {what}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Try("cpu", () => c.Set("cpu", BuildRules.Cpus(s.Cpus())));
        Try("motherboard", () => { var (m, p) = s.Board(); c.Set("motherboard", BuildRules.Motherboard(m, p)); });
        Try("ram", () => c.Set("ram", BuildRules.Ram(s.Memory())));
        Try("gpu", () => c.Set("gpu", BuildRules.Gpus(s.Gpus())));
        Try("storage", () =>
        {
            var (ssd, hdd) = BuildRules.Disks(s.Disks());
            c.Set("ssd", ssd);
            c.Set("hdd", hdd);
        });
        Try("monitor", () => c.Set("monitor", BuildRules.Monitors(s.Monitors())));
        Try("optical-drive", () => c.Set("optical-drive", BuildRules.Opticals(s.Opticals())));
        return c;
    }
}

/// <summary>WMI・DXGI から取ってくるだけ。直す・選ぶのは <see cref="BuildRules"/>。</summary>
internal static class WindowsReaders
{
    private const string StorageNs = @"\\.\root\microsoft\windows\storage";
    private const string WmiNs = @"\\.\root\wmi";

    public static IEnumerable<string> Cpus() => SystemInfo.SystemCheck.ReadCpus().Select(x => x.Name).ToList();

    public static (string?, string?) Board()
    {
        using var q = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        foreach (ManagementObject o in q.Get())
            return (o["Manufacturer"] as string, o["Product"] as string);
        return (null, null);
    }

    public static IEnumerable<RawMemory> Memory()
    {
        // SMBIOSMemoryType・ConfiguredClockSpeed は新しい Windows から。無い古い機械では WMI がクエリごと断るので、
        // そのときは無い欄を外して読み直す（最後の形は Windows 7 にもある欄だけ）
        const string Base = "PartNumber, Manufacturer, Capacity, Speed, MemoryType";
        var queries = new (string Sql, bool Smbios, bool Configured)[]
        {
            ($"SELECT {Base}, SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory", true, true),
            ($"SELECT {Base}, SMBIOSMemoryType FROM Win32_PhysicalMemory", true, false),
            ($"SELECT {Base} FROM Win32_PhysicalMemory", false, false),
        };
        var list = new List<RawMemory>();
        for (int i = 0; i < queries.Length; i++)
        {
            var (sql, smbios, configured) = queries[i];
            try
            {
                using var q = new ManagementObjectSearcher(sql);
                foreach (ManagementObject o in q.Get())
                    list.Add(new RawMemory(o["PartNumber"] as string ?? "", o["Manufacturer"] as string ?? "", ToULong(o["Capacity"]),
                                           (uint)ToInt(o["Speed"]), smbios ? ToInt(o["SMBIOSMemoryType"]) : 0, ToInt(o["MemoryType"]),
                                           configured ? (uint)ToInt(o["ConfiguredClockSpeed"]) : 0));
                return list;
            }
            catch (ManagementException) when (i < queries.Length - 1) { list.Clear(); }
        }
        return list;
    }

    /// <summary>
    /// DXGI（点検の負荷テストと同じ一覧。VRAM が 4GB で頭打ちにならず、仮想ディスプレイの別名も除いてある）。
    /// DXGI が何も返さなければ Win32_VideoController（VRAM は書かない。AdapterRAM は 4GB で頭打ちのため）。
    /// </summary>
    public static IEnumerable<RawGpu> Gpus()
    {
        var dxgi = GpuAdapters.Enumerate(null);
        if (dxgi.Count > 0) return dxgi.Select(a => new RawGpu(a.Name, a.DedicatedBytes, a.PciBus is not null)).ToList();

        var list = new List<RawGpu>();
        using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
        foreach (ManagementObject o in q.Get())
        {
            string pnp = o["PNPDeviceID"] as string ?? "";
            list.Add(new RawGpu(o["Name"] as string ?? "", 0, pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>MSFT_PhysicalDisk（接続と SSD/HDD の別が取れる）。読めなければ Win32_DiskDrive。</summary>
    public static IEnumerable<RawDisk> Disks()
    {
        try
        {
            var list = new List<RawDisk>();
            using var q = new ManagementObjectSearcher(StorageNs, "SELECT FriendlyName, Model, BusType, MediaType, Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in q.Get())
                list.Add(new RawDisk(o["FriendlyName"] as string ?? o["Model"] as string ?? "", ToInt(o["BusType"]), ToInt(o["MediaType"]), ToULong(o["Size"])));
            if (list.Count > 0) return list;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning($"PartsLife: build code: MSFT_PhysicalDisk unavailable, falling back to Win32_DiskDrive: {ex.Message}");
        }

        var old = new List<RawDisk>();
        using var w = new ManagementObjectSearcher("SELECT Model, Size, InterfaceType, PNPDeviceID, MediaType FROM Win32_DiskDrive");
        foreach (ManagementObject o in w.Get())
        {
            int bus = BuildRules.BusFromWin32(o["InterfaceType"] as string, o["PNPDeviceID"] as string, o["MediaType"] as string);
            old.Add(new RawDisk(o["Model"] as string ?? "", bus, 0, ToULong(o["Size"])));
        }
        return old;
    }

    /// <summary>WmiMonitorID（EDID の名前）。名前の入っていないモニターは数えない。</summary>
    public static IEnumerable<RawMonitor> Monitors()
    {
        var list = new List<RawMonitor>();
        using var q = new ManagementObjectSearcher(WmiNs, "SELECT UserFriendlyName, Active FROM WmiMonitorID");
        foreach (ManagementObject o in q.Get())
            list.Add(new RawMonitor(BuildRules.DecodeWmiString(o["UserFriendlyName"] as ushort[]), o["Active"] as bool? ?? true));
        return list;
    }

    public static IEnumerable<RawOptical> Opticals()
    {
        var list = new List<RawOptical>();
        using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_CDROMDrive");
        foreach (ManagementObject o in q.Get())
            list.Add(new RawOptical(o["Name"] as string ?? "", o["PNPDeviceID"] as string ?? ""));
        return list;
    }

    private static int ToInt(object? o) { try { return o is null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
    private static ulong ToULong(object? o) { try { return o is null ? 0 : Convert.ToUInt64(o); } catch { return 0; } }
}
