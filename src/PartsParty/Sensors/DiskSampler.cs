using System.IO;
using System.Management;

namespace PartsParty.Sensors;

public sealed record DriveInfoLite(
    string Name,        // "C:"
    string Label,       // ボリューム名（空のことも多い）
    ulong UsedBytes,
    ulong TotalBytes,
    bool IsSsd);        // 分からない場合は false（HDD 扱い）

/// <summary>
/// ドライブの容量と、SSD か HDD かを見る。
///
/// ■ 容量は DriveInfo で足りる。権限も要らない。
/// ■ SSD/HDD の判別は MSFT_PhysicalDisk.MediaType（4=SSD, 3=HDD）を見る。
///   ここは WMI なので遅い。**起動時に1回だけ**読んで覚えておく。
///   毎秒読むと、それだけで常駐アプリの負荷目標を割る。
/// </summary>
public sealed class DiskSampler
{
    private static readonly Dictionary<string, bool> EmptyMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>判別が失敗した理由。--probe で読むためだけに残す（通常の動作には影響しない）。</summary>
    public static string? LastProbeError;

    /// <summary>
    /// "C:" -> SSDか。**別スレッドで1回だけ引く。**
    /// WMI は数秒かかることがあり、UI スレッドで引くと起動時に固まって見える。
    /// 出るまでは全部 HDD 表示になるが、数秒で正しくなる。
    /// </summary>
    private readonly Task<Dictionary<string, bool>> _ssdProbe = Task.Run(ProbeMediaTypes);

    public IReadOnlyList<DriveInfoLite> Sample()
    {
        var ssd = _ssdProbe.IsCompletedSuccessfully ? _ssdProbe.Result : EmptyMap;

        var list = new List<DriveInfoLite>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string name = d.Name.TrimEnd('\\');
                ulong total = (ulong)d.TotalSize;
                ulong free = (ulong)d.TotalFreeSpace;
                list.Add(new DriveInfoLite(
                    name, d.VolumeLabel ?? "", total - free, total,
                    ssd.GetValueOrDefault(name, false)));
            }
            catch (IOException) { /* 取り外された等。黙って飛ばす */ }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// ドライブ文字 → SSDかどうか。
    ///
    /// **MSFT_Partition と MSFT_PhysicalDisk の2本だけで引く。**
    /// 最初は Win32_DiskDriveToDiskPartition から ASSOCIATORS で辿っていたが、
    /// 実機で全ドライブが HDD 判定になった。あの経路は WQL に
    /// `Disk #0, Partition #1` のような引用符入りの値を埋め込む必要があり、崩れやすい。
    ///
    /// 実機での正解（照合済み）:
    ///   D → disk0 SSD / E → disk1 HDD / F → disk2 SSD / C → disk3 SSD
    ///
    /// 判別できなければ全部 HDD 扱いにする（表示が少し違うだけで害はない）。
    /// </summary>
    private static Dictionary<string, bool> ProbeMediaTypes()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string ns = @"\\.\root\microsoft\windows\storage";

            // 物理ディスク番号 → SSDか
            var ssdByDisk = new Dictionary<uint, bool>();
            using (var disks = new ManagementObjectSearcher(ns,
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject d in disks.Get())
                {
                    if (!uint.TryParse(d["DeviceId"]?.ToString(), out uint id)) continue;
                    ushort media = Convert.ToUInt16(d["MediaType"] ?? (ushort)0);
                    ssdByDisk[id] = media == 4 || media == 5;   // 3=HDD, 4=SSD, 5=SCM
                }
            }

            // ドライブ文字 → 物理ディスク番号
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
                result[$"{char.ToUpperInvariant(letter)}:"] = ssdByDisk.GetValueOrDefault(disk, false);
            }
        }
        catch (Exception ex) { LastProbeError = ex.GetType().Name + ": " + ex.Message; }
        return result;
    }
}
