using System.Text;
using System.Text.RegularExpressions;

namespace PartsLife.Checks.Build;

/// <summary>Win32_PhysicalMemory の1枚（取ってきたままの値）。</summary>
public sealed record RawMemory(string PartNumber, string Manufacturer, ulong Capacity, uint Speed, int SmbiosType, int MemoryType);

/// <summary>GPU 1枚。DXGI（VRAM が正しく取れる）か、取れなければ Win32_VideoController。</summary>
/// <param name="OnPciBus">PCI の番地がある（＝本物の板）。仮想ディスプレイ・リモートの GPU には無い。</param>
public sealed record RawGpu(string Name, ulong DedicatedBytes, bool OnPciBus);

/// <summary>記憶装置1台。<see cref="BusType"/>・<see cref="MediaType"/> は MSFT_PhysicalDisk の番号で持つ。</summary>
public sealed record RawDisk(string Name, int BusType, int MediaType, ulong Size);

/// <summary>モニター1台（WmiMonitorID）。</summary>
public sealed record RawMonitor(string Name, bool Active);

/// <summary>光学ドライブ1台（Win32_CDROMDrive）。</summary>
public sealed record RawOptical(string Name, string PnpDeviceId);

/// <summary>
/// 取ってきた値を、構成コードの部品に直す決まり。**全部純粋な関数**（WMI に触らない）。
/// 実機を持たなくても、試験で中身を確かめられるようにするため。
/// </summary>
public static class BuildRules
{
    // =====================================================================
    // 名前
    // =====================================================================

    /// <summary>前後の空白を除き、空白の並びを1つに、制御文字（NUL など）を除いて、80 文字まで。</summary>
    public static string CleanName(string? s, int max = 80)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        string r = sb.ToString();
        return r.Length <= max ? r : r[..max].TrimEnd();
    }

    /// <summary>
    /// BIOS やドライバが「値なし」の代わりに入れてくる決まり文句。型番として送っても意味が無い。
    /// </summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.", "To Be Filled By O.E.M.", "Default string", "System Product Name", "System manufacturer",
        "Base Board Product Name", "Base Board Manufacturer", "Not Applicable", "Not Specified", "Not Available",
        "None", "Unknown", "Undefined", "N/A", "NA", "Manufacturer", "Product Name", "Type2 - Board Product Name1",
        "Type2 - Board Vendor Name1", "OEM", "O.E.M.", "Default",
    };

    private static readonly Regex Zeros = new("^(0x)?[0F]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>値として使えない（空・決まり文句・0 や F の並び）。</summary>
    public static bool IsPlaceholder(string? s)
    {
        string c = CleanName(s);
        return c.Length == 0 || Placeholders.Contains(c) || Zeros.IsMatch(c);
    }

    /// <summary>
    /// シリアル番号に見えるか。**型番の代わりにシリアルが入ってくる機種がある**ので、最後の網として落とす。
    ///
    /// 空白が無く 16 文字を超え、かつ次のどちらか:
    /// - 区切り（- _ . :）を除いた文字の 9 割以上が 16 進の数字（NVMe のシリアル・UUID・16 進に直されたシリアル）
    /// - 10 桁以上の数字の連なりを含む
    ///
    /// 型番も空白なしで長いことがある（Seagate の ST2000DM008-2FR102、Corsair の CMK64GX5M2B5600Z40）。
    /// これらは 16 進以外の英字を含み、長い数字の連なりも無いので残る。
    /// </summary>
    public static bool LooksLikeSerial(string? s)
    {
        string c = CleanName(s, int.MaxValue);
        if (c.Length <= 16 || c.Contains(' ')) return false;
        int hex = 0, total = 0, run = 0, longest = 0;
        foreach (char ch in c)
        {
            if (char.IsAsciiDigit(ch)) { run++; longest = Math.Max(longest, run); }
            else run = 0;
            if (ch is '-' or '_' or '.' or ':') continue;
            total++;
            if (char.IsAsciiHexDigit(ch)) hex++;
        }
        return longest >= 10 || (total > 0 && hex >= total * 0.9);
    }

    // =====================================================================
    // CPU・マザーボード
    // =====================================================================

    public static List<BuildPart> Cpus(IEnumerable<string> names) =>
        names.Select(n => CleanName(n)).Where(n => !IsPlaceholder(n)).Select(n => new BuildPart(n)).ToList();

    /// <summary>名前は「メーカー 型番」（受け手はこの形で照合する）。型番が決まり文句なら出さない。</summary>
    public static List<BuildPart> Motherboard(string? manufacturer, string? product)
    {
        string p = CleanName(product);
        if (IsPlaceholder(p)) return new();
        string m = IsPlaceholder(manufacturer) ? "" : CleanName(manufacturer, 40);
        string name = m.Length > 0 && !p.StartsWith(m, StringComparison.OrdinalIgnoreCase) ? $"{m} {p}" : p;
        return new() { new BuildPart(CleanName(name), Mfr: m.Length > 0 ? m : null) };
    }

    // =====================================================================
    // メモリ
    // =====================================================================

    /// <summary>
    /// SMBIOS の Memory Device（Type 17）の Memory Type。Win32_PhysicalMemory.SMBIOSMemoryType に入っている。
    /// 0 や 2（Unknown）なら、古い MemoryType（DDR3 までしか持たない）を見る。
    /// </summary>
    public static string? RamType(int smbiosType, int memoryType = 0) => smbiosType switch
    {
        0x12 => "DDR",
        0x13 or 0x14 => "DDR2",
        0x18 or 0x19 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => memoryType switch
        {
            20 => "DDR",
            21 or 22 => "DDR2",
            24 or 25 => "DDR3",
            26 => "DDR4",
            _ => null,
        },
    };

    /// <summary>
    /// JEDEC のメーカー番号で返してくる機種がある（"80CE" や "CE00000000000000"）。よく見る物だけ名前に直す。
    /// 知らない番号は書かない（番号のまま送っても受け手には読めない）。
    /// </summary>
    private static readonly Dictionary<string, string> Jedec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE"] = "Samsung",
        ["80AD"] = "SK hynix", ["AD"] = "SK hynix",
        ["802C"] = "Micron", ["2C"] = "Micron",
        ["0198"] = "Kingston", ["9801"] = "Kingston",
        ["029E"] = "Corsair", ["9E02"] = "Corsair",
        ["04CD"] = "G.Skill", ["CD04"] = "G.Skill",
        ["859B"] = "Crucial", ["059B"] = "Crucial", ["9B05"] = "Crucial",
        ["04CB"] = "ADATA", ["CB04"] = "ADATA",
        ["04EF"] = "Team Group", ["EF04"] = "Team Group",
    };

    private static readonly Regex HexOnly = new("^[0-9A-F]{2,16}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? RamManufacturer(string? raw)
    {
        string m = CleanName(raw, 40);
        if (IsPlaceholder(m)) return null;
        if (!HexOnly.IsMatch(m)) return m;
        string key = m.TrimEnd('0');
        if (key.Length % 2 == 1) key += "0";     // "9E02000…" の末尾の 0 を削りすぎない
        return Jedec.TryGetValue(key, out var name) ? name : Jedec.GetValueOrDefault(m.Length >= 4 ? m[..4] : m);
    }

    /// <summary>
    /// 1枚ずつ部品にする（同じ物をまとめるのは <see cref="BuildCode"/> の側）。
    /// 型番が読めない・シリアルに見えるときは「DDR4 16GB」の形で名前を作る（容量も無ければ出さない）。
    /// </summary>
    public static List<BuildPart> Ram(IEnumerable<RawMemory> modules)
    {
        var list = new List<BuildPart>();
        foreach (var m in modules)
        {
            int? cap = m.Capacity > 0 ? (int)Math.Round(m.Capacity / 1073741824.0) : null;
            if (cap == 0) cap = null;
            string? type = RamType(m.SmbiosType, m.MemoryType);
            string name = CleanName(m.PartNumber);
            if (IsPlaceholder(name) || LooksLikeSerial(name))
            {
                if (cap is null) continue;
                name = $"{type ?? "RAM"} {cap}GB".Trim();
            }
            list.Add(new BuildPart(name, Cap: cap, Speed: m.Speed > 0 ? (int)m.Speed : null, Type: type, Mfr: RamManufacturer(m.Manufacturer)));
        }
        return list;
    }

    // =====================================================================
    // GPU
    // =====================================================================

    /// <summary>
    /// 本物の板ではない表示装置。仮想ディスプレイ（VR のストリーミング・リモートデスクトップ）・
    /// 標準のドライバ・仮想マシンの画面。
    /// </summary>
    private static readonly string[] VirtualWords =
    {
        "basic display", "basic render", "基本ディスプレイ", "hyper-v", "virtual", "vmware", "virtualbox", "parsec",
        "remote display", "remote desktop", "indirect display", "iddsample", "spacedesk", "citrix", "qxl", "red hat",
        "splashtop", "displaylink", "mirage driver", "oculus",
    };

    public static bool IsVirtualDevice(string name)
    {
        string n = name.ToLowerInvariant();
        return VirtualWords.Any(w => n.Contains(w));
    }

    /// <summary>
    /// CPU に入っている GPU（AMD の Radeon(TM) Graphics・780M、Intel の UHD・Iris・Arc(TM) Graphics）。
    /// **板ではないので構成の GPU には入れない**（入れると BTO の比較で「GPU あり」と取り違える）。
    /// 単体の板の名前は型番の後ろに "Graphics" が付かない（Radeon RX 7900 XTX、Arc A770 は付くが番号が入る）。
    /// </summary>
    private static readonly Regex Integrated = new(
        @"(Radeon(\s*\(TM\))?\s+((\d{3,4}M|Vega\s*\d*|R[2-7])\s+)?Graphics\s*$)" +
        @"|(Intel.*\b(U?HD|Iris)\b.*Graphics)" +
        @"|(Intel.*Arc(\s*\(TM\))?\s+(Graphics\s*$|\d{3}[VTH]\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsIntegratedGpu(string name) => Integrated.IsMatch(CleanName(name));

    public static List<BuildPart> Gpus(IEnumerable<RawGpu> gpus)
    {
        var list = new List<BuildPart>();
        foreach (var g in gpus)
        {
            string name = CleanName(g.Name);
            if (!g.OnPciBus || IsPlaceholder(name) || IsVirtualDevice(name) || IsIntegratedGpu(name)) continue;
            int vram = (int)Math.Round(g.DedicatedBytes / 1073741824.0);
            list.Add(new BuildPart(name, Vram: vram > 0 ? vram : null));
        }
        return list;
    }

    // =====================================================================
    // 記憶装置
    // =====================================================================

    /// <summary>MSFT_PhysicalDisk.BusType のうち、機械の中の部品として数える物と、構成コードでの書き方。</summary>
    public static string? StorageInterface(int busType) => busType switch
    {
        17 => "nvme",
        11 or 3 => "sata",   // 3 = ATA（SATA を IDE 互換で動かしている物もここに来る）
        10 => "sas",
        _ => null,
    };

    /// <summary>
    /// 部品として数えない接続: USB（外付け・USB メモリ）・SD/MMC・仮想ディスク・記憶域スペース・
    /// iSCSI/ファイバーチャネル（ネットワークの向こう）・ATAPI（光学ドライブ）。
    /// </summary>
    public static bool IsExcludedBus(int busType) => busType is 2 or 6 or 7 or 9 or 12 or 13 or 14 or 15 or 16;

    /// <summary>
    /// Win32_DiskDrive（MSFT_PhysicalDisk が読めないときの代わり）の接続を、BusType の番号に寄せる。
    /// </summary>
    public static int BusFromWin32(string? interfaceType, string? pnpDeviceId, string? mediaType)
    {
        string pnp = pnpDeviceId ?? "";
        if (pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)) return 7;
        if ((mediaType ?? "").Contains("External", StringComparison.OrdinalIgnoreCase)
            || (mediaType ?? "").Contains("Removable", StringComparison.OrdinalIgnoreCase)) return 7;
        if (pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase)) return 17;
        return (interfaceType ?? "").ToUpperInvariant() switch
        {
            "USB" or "1394" => 7,
            "IDE" => 11,
            "SCSI" => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// 10 進の GB。売られている容量に近ければ（5% 以内）そちらに寄せる
    /// （1,024,209,543,168 バイトの「1TB」は 1000、512,110,190,592 バイトは 512）。
    /// </summary>
    public static int StorageGb(ulong bytes)
    {
        double gb = bytes / 1e9;
        int best = 0;
        double bestDiff = double.MaxValue;
        foreach (int s in MarketSizes)
        {
            double diff = Math.Abs(gb - s) / s;
            if (diff <= 0.05 && diff < bestDiff) { best = s; bestDiff = diff; }
        }
        return best > 0 ? best : (int)Math.Round(gb);
    }

    private static readonly int[] MarketSizes =
    {
        32, 64, 120, 128, 240, 250, 256, 320, 480, 500, 512, 750, 960, 1000, 1500, 1920, 2000, 3000, 3840, 4000,
        5000, 6000, 7680, 8000, 10000, 12000, 14000, 16000, 18000, 20000, 22000, 24000, 26000, 28000, 30000, 32000,
    };

    /// <summary>
    /// SSD か HDD か。MediaType（4=SSD・5=SCM・3=HDD）が分かればそれ、分からなければ
    /// NVMe は SSD、名前に SSD / NVMe / Solid State があれば SSD、それ以外は HDD。
    /// </summary>
    public static bool IsSsd(RawDisk d)
    {
        if (d.MediaType is 4 or 5) return true;
        if (d.MediaType == 3) return false;
        if (d.BusType == 17) return true;
        string n = d.Name.ToUpperInvariant();
        return n.Contains("SSD") || n.Contains("NVME") || n.Contains("SOLID STATE");
    }

    public static (List<BuildPart> Ssd, List<BuildPart> Hdd) Disks(IEnumerable<RawDisk> disks)
    {
        var ssd = new List<BuildPart>();
        var hdd = new List<BuildPart>();
        foreach (var d in disks)
        {
            string name = CleanName(d.Name);
            if (IsExcludedBus(d.BusType) || d.Size == 0 || IsPlaceholder(name) || IsVirtualDevice(name)) continue;
            int gb = StorageGb(d.Size);
            var part = new BuildPart(name, Cap: gb > 0 ? gb : null, Type: StorageInterface(d.BusType));
            (IsSsd(d) ? ssd : hdd).Add(part);
        }
        return (ssd, hdd);
    }

    // =====================================================================
    // モニター・光学ドライブ
    // =====================================================================

    /// <summary>WmiMonitorID の UserFriendlyName などは UInt16 の並び（1 要素 1 文字、0 で終わり）。</summary>
    public static string DecodeWmiString(ushort[]? raw)
    {
        if (raw is null) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (ushort u in raw)
        {
            if (u == 0) break;
            sb.Append((char)u);
        }
        return CleanName(sb.ToString());
    }

    public static List<BuildPart> Monitors(IEnumerable<RawMonitor> monitors) =>
        monitors.Where(m => m.Active)
                .Select(m => CleanName(m.Name))
                .Where(n => !IsPlaceholder(n) && !IsVirtualDevice(n))
                .Select(n => new BuildPart(n)).ToList();

    /// <summary>光学ドライブ。ISO を開いたときの仮想ドライブ（Msft Virtual DVD-ROM）と USB の外付けは数えない。</summary>
    public static List<BuildPart> Opticals(IEnumerable<RawOptical> drives) =>
        drives.Where(d => !d.PnpDeviceId.Contains("MSFT", StringComparison.OrdinalIgnoreCase)
                          && !d.PnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
              .Select(d => CleanName(d.Name))
              .Where(n => !IsPlaceholder(n) && !IsVirtualDevice(n))
              .Select(n => new BuildPart(n)).ToList();
}
