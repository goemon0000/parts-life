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
    /// パーティション→ディスク→物理ディスクと辿る必要があるので、素直に書くと長い。
    /// 失敗したら全部 HDD 扱いにする（表示が少し違うだけで害はない）。
    /// </summary>
    private static Dictionary<string, bool> ProbeMediaTypes()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 物理ディスク番号 -> SSDか
            var ssdByIndex = new Dictionary<uint, bool>();
            using (var searcher = new ManagementObjectSearcher(
                @"\\.\root\microsoft\windows\storage", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    if (!uint.TryParse(disk["DeviceId"]?.ToString(), out uint id)) continue;
                    ushort media = Convert.ToUInt16(disk["MediaType"] ?? (ushort)0);
                    ssdByIndex[id] = media == 4;      // 3=HDD, 4=SSD, 5=SCM, 0=不明
                }
            }

            // ディスク番号 -> ドライブ文字
            using var partSearcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject link in partSearcher.Get())
            {
                string drivePath = link["Antecedent"]?.ToString() ?? "";
                string partPath = link["Dependent"]?.ToString() ?? "";
                int idx = ExtractDiskIndex(drivePath);
                if (idx < 0) continue;

                using var logical = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{{partPath}}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                foreach (ManagementObject ld in logical.Get())
                {
                    string letter = ld["DeviceID"]?.ToString() ?? "";
                    if (letter.Length >= 2) result[letter] = ssdByIndex.GetValueOrDefault((uint)idx, false);
                }
            }
        }
        catch { /* 判別できなくても容量は出せる */ }
        return result;
    }

    private static int ExtractDiskIndex(string deviceIdPath)
    {
        // 例: \\PC\root\cimv2:Win32_DiskDrive.DeviceID="\\\\.\\PHYSICALDRIVE0"
        int at = deviceIdPath.IndexOf("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        var digits = new string(deviceIdPath[(at + "PHYSICALDRIVE".Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : -1;
    }
}
