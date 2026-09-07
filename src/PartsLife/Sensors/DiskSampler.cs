using System.IO;
using System.Management;

namespace PartsLife.Sensors;

/// <summary>物理ディスク1台。<paramref name="Number"/> は PDH のインスタンス名に出る番号。</summary>
public sealed record PhysicalDisk(uint Number, bool IsSsd, bool IsNvme);

public sealed record DriveInfoLite(
    string Name,        // "C:"
    string Label,       // ボリューム名（空のことも多い）
    ulong UsedBytes,
    ulong TotalBytes,
    bool IsSsd,
    bool IsNvme,
    uint DiskNumber);

/// <summary>
/// ドライブの容量と、SSD / HDD / M.2(NVMe) の別を見る。
///
/// ■ 容量は DriveInfo で足りる。権限も要らない。
/// ■ 種別は MSFT_PhysicalDisk の MediaType（3=HDD, 4=SSD）と
///   BusType（17=NVMe）を見る。**NVMe かどうかまで見ないと、
///   M.2 と 2.5インチ SATA SSD が同じ扱いになる。**
/// ■ 対応付けは MSFT_Partition の DriveLetter と DiskNumber。
///   Win32_DiskDriveToDiskPartition から ASSOCIATORS で辿る書き方は、
///   実機で全ドライブ誤判定になった（WQL に引用符入りの値を埋める必要があり崩れる）。
/// ■ WMI は遅い。**起動時に別スレッドで1回だけ**引いて覚えておく。
/// </summary>
public sealed class DiskSampler
{
    private static readonly Dictionary<string, bool> EmptyMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>判別が失敗した理由。--probe で読むためだけに残す（通常の動作には影響しない）。</summary>
    public static string? LastProbeError;

    private sealed record Probe(
        Dictionary<uint, PhysicalDisk> Disks,       // ディスク番号 -> 種別
        Dictionary<string, uint> DriveToDisk);      // "C:" -> ディスク番号

    private static readonly Probe Empty = new(new(), new(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// **別スレッドで1回だけ引く。**
    /// WMI は数秒かかることがあり、UI スレッドで引くと起動時に固まって見える。
    /// 出るまでは全部 HDD 表示になるが、数秒で正しくなる。
    /// </summary>
    private readonly Task<Probe> _probe = Task.Run(ProbeDisks);

    private Probe Current => _probe.IsCompletedSuccessfully ? _probe.Result : Empty;

    /// <summary>実機に載っている物理ディスク。並べる顔ぶれを決めるのに使う。</summary>
    public IReadOnlyCollection<PhysicalDisk> Disks => Current.Disks.Values;

    public IReadOnlyList<DriveInfoLite> Sample()
    {
        var probe = Current;
        var list = new List<DriveInfoLite>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string name = d.Name.TrimEnd('\\');
                ulong total = (ulong)d.TotalSize;
                ulong free = (ulong)d.TotalFreeSpace;

                uint number = probe.DriveToDisk.GetValueOrDefault(name, uint.MaxValue);
                var disk = probe.Disks.GetValueOrDefault(number);
                list.Add(new DriveInfoLite(name, d.VolumeLabel ?? "", total - free, total,
                                           disk?.IsSsd ?? false, disk?.IsNvme ?? false, number));
            }
            catch (IOException) { /* 取り外された等。黙って飛ばす */ }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static Probe ProbeDisks()
    {
        var disks = new Dictionary<uint, PhysicalDisk>();
        var driveToDisk = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string ns = @"\\.\root\microsoft\windows\storage";

            using (var q = new ManagementObjectSearcher(ns,
                "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject d in q.Get())
                {
                    if (!uint.TryParse(d["DeviceId"]?.ToString(), out uint id)) continue;
                    ushort media = Convert.ToUInt16(d["MediaType"] ?? (ushort)0);
                    ushort bus = Convert.ToUInt16(d["BusType"] ?? (ushort)0);
                    bool ssd = media == 4 || media == 5;          // 3=HDD, 4=SSD, 5=SCM
                    disks[id] = new PhysicalDisk(id, ssd, ssd && bus == 17);   // 17=NVMe
                }
            }

            using var parts = new ManagementObjectSearcher(ns,
                "SELECT DriveLetter, DiskNumber FROM MSFT_Partition");
            foreach (ManagementObject p in parts.Get())
            {
                // DriveLetter は文字コード（ushort）で返る。0 は文字が付いていない領域
                var raw = p["DriveLetter"];
                if (raw is null) continue;
                char letter = raw is char c ? c : (char)Convert.ToUInt16(raw);
                if (!char.IsLetter(letter)) continue;
                if (!uint.TryParse(p["DiskNumber"]?.ToString(), out uint disk)) continue;
                driveToDisk[$"{char.ToUpperInvariant(letter)}:"] = disk;
            }
        }
        catch (Exception ex) { LastProbeError = ex.GetType().Name + ": " + ex.Message; }

        return new Probe(disks, driveToDisk);
    }
}
