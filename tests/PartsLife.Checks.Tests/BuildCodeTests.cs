using System.Text;
using System.Text.Json;
using PartsLife.Checks.Build;
using Xunit.Abstractions;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 構成コード（web-autoai.com の /my-config に貼る文字列）。**受け手と同時に作っている約束なので、形そのものを確かめる。**
/// </summary>
public class BuildCodeFormatTests
{
    private static BuildConfig Config(params (string Cat, BuildPart Part)[] parts)
    {
        var c = new BuildConfig();
        foreach (var g in parts.GroupBy(p => p.Cat)) c.Set(g.Key, g.Select(p => p.Part));
        return c;
    }

    private static JsonElement Parse(string code) => JsonDocument.Parse(BuildCode.Decode(code)).RootElement;

    [Fact]
    public void Code_is_prefix_plus_base64url_of_utf8_json_and_round_trips()
    {
        // ~ と ? の並びは、普通の base64 なら + と / になる（base64url で - と _ に置き換わることを確かめるため）
        var c = Config(("cpu", new BuildPart("AMD Ryzen 7 5800X 8-Core Processor ~~~~~~ ??????")),
                       ("ram", new BuildPart("F4-3200C16-16GVK", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "G.Skill", Q: 2)),
                       ("monitor", new BuildPart("ＤＥＬＬ 日本語の名前")));
        var r = BuildCode.Make(c);

        Assert.StartsWith("PCB1.", r.Code);
        string body = r.Code["PCB1.".Length..];
        Assert.Matches("^[A-Za-z0-9_-]+$", body);   // パディング（=）も + / も無い

        string json = BuildCode.ToJson(r.Config, indented: false);
        string standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Contains("+", standard);
        Assert.Contains("/", standard);
        Assert.Equal(standard.TrimEnd('=').Replace('+', '-').Replace('/', '_'), body);

        Assert.Equal(json, BuildCode.Decode(r.Code));
        var root = Parse(r.Code);
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("parts-life", root.GetProperty("src").GetString());
        var ram = root.GetProperty("p").GetProperty("ram")[0];
        Assert.Equal("F4-3200C16-16GVK", ram.GetProperty("n").GetString());
        Assert.Equal(16, ram.GetProperty("cap").GetInt32());
        Assert.Equal(3200, ram.GetProperty("spd").GetInt32());
        Assert.Equal("DDR4", ram.GetProperty("t").GetString());
        Assert.Equal("G.Skill", ram.GetProperty("mfr").GetString());
        Assert.Equal(2, ram.GetProperty("q").GetInt32());
        Assert.Equal("ＤＥＬＬ 日本語の名前", root.GetProperty("p").GetProperty("monitor")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void Json_has_only_known_keys_omits_empty_categories_and_q_of_one()
    {
        var c = Config(("gpu", new BuildPart("NVIDIA GeForce RTX 3080 Ti", Vram: 12)));
        c.Set("ssd", Array.Empty<BuildPart>());
        string json = BuildCode.ToJson(BuildCode.Normalize(c), indented: false);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{"gpu":[{"n":"NVIDIA GeForce RTX 3080 Ti","vram":12}]}}""", json);
    }

    [Fact]
    public void Saved_json_is_the_same_content_pretty_printed()
    {
        var r = BuildCode.Make(Config(("cpu", new BuildPart("AMD Ryzen 9 7950X 16-Core Processor"))));
        Assert.Contains("\n", r.Json);
        Assert.Equal(BuildCode.Decode(r.Code), JsonSerializeCompact(r.Json));
    }

    private static string JsonSerializeCompact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) }))
            doc.WriteTo(w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Fact]
    public void Decode_rejects_other_strings()
    {
        Assert.Throws<FormatException>(() => BuildCode.Decode("PCB2.abc"));
        Assert.Throws<FormatException>(() => BuildCode.Decode("PCB1.!!!"));
    }

    [Fact]
    public void Identical_parts_aggregate_with_q_capped_at_16()
    {
        var stick = new BuildPart("CMK64GX5M2B5600Z40", Cap: 32, Speed: 4800, Type: "DDR5", Mfr: "Corsair");
        var c = new BuildConfig();
        c.Set("ram", new[] { stick, stick, stick with { Cap = 16 } });
        c.Set("ssd", Enumerable.Repeat(new BuildPart("Samsung SSD 980 PRO 1TB", Cap: 1000, Type: "nvme"), 20));
        var n = BuildCode.Normalize(c);

        Assert.Equal(2, n["ram"].Count);
        Assert.Equal(2, n["ram"][0].Q);
        Assert.Equal(1, n["ram"][1].Q);
        Assert.Equal(3, n.Count("ram"));
        Assert.Equal(16, Assert.Single(n["ssd"]).Q);
    }

    [Fact]
    public void Names_are_trimmed_collapsed_and_capped()
    {
        var c = Config(("cpu", new BuildPart("  AMD   Ryzen 9\t7950X 16-Core Processor            \0")),
                       ("monitor", new BuildPart(new string('M', 100))),
                       ("ram", new BuildPart("X", Type: "LPDDR5XLONG", Mfr: new string('m', 60))));
        var n = BuildCode.Normalize(c);
        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", n["cpu"][0].Name);
        Assert.Equal(80, n["monitor"][0].Name.Length);
        Assert.Equal(8, n["ram"][0].Type!.Length);
        Assert.Equal(40, n["ram"][0].Mfr!.Length);
    }

    [Fact]
    public void At_most_eight_kinds_per_category_with_a_note()
    {
        var c = new BuildConfig();
        c.Set("ssd", Enumerable.Range(1, 10).Select(i => new BuildPart($"SSD model {i}", Cap: 1000, Type: "nvme")));
        var r = BuildCode.Make(c);
        Assert.Equal(8, r.Config["ssd"].Count);
        Assert.Equal("SSD model 8", r.Config["ssd"][^1].Name);
        Assert.Contains(r.Notes, x => x.Contains("8 種類"));
    }

    // --- 6,000 字の上限 ---

    private static IEnumerable<BuildPart> Long(string tag, int count) =>
        Enumerable.Range(1, count).Select(i => new BuildPart($"{tag} {i:00} ".PadRight(80, 'x'), Cap: 2000, Type: "sata", Mfr: new string('m', 40)));

    private static int Length(BuildConfig c) => BuildCode.Encode(c).Length - BuildCode.Prefix.Length;

    /// <summary>名前 80 字・メーカー 40 字の部品を並べた、ありえないほど大きな構成（1 部品でコード約 210 字）。</summary>
    private static BuildConfig Big(int cpus, int monitors, int hdds, int rams)
    {
        var c = new BuildConfig();
        c.Set("cpu", Long("cpu", cpus));
        c.Set("gpu", Long("gpu", 8));
        c.Set("ssd", Long("ssd", 8));
        c.Set("ram", Long("ram", rams));
        c.Set("hdd", Long("hdd", hdds));
        c.Set("monitor", Long("monitor", monitors));
        return c;
    }

    [Fact]
    public void Over_the_limit_monitors_go_first()
    {
        var c = Big(cpus: 1, monitors: 8, hdds: 8, rams: 2);
        var withoutMonitors = c.Clone();
        withoutMonitors.Remove("monitor");
        Assert.True(Length(c) > BuildCode.MaxLength);                 // 前提: このままでは長すぎる
        Assert.True(Length(withoutMonitors) <= BuildCode.MaxLength);  // 前提: モニターを除けば収まる

        var r = BuildCode.Make(c);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
        Assert.Empty(r.Config["monitor"]);
        Assert.Equal(8, r.Config["hdd"].Count);
        Assert.Equal(2, r.Config["ram"].Count);
        Assert.Contains("モニター", Assert.Single(r.Notes));
        Assert.DoesNotContain("monitor", BuildCode.Decode(r.Code));
    }

    [Fact]
    public void Then_hdds_then_extra_ram_kinds()
    {
        var c = Big(cpus: 8, monitors: 8, hdds: 8, rams: 8);
        var keptRamOnly = c.Clone();
        keptRamOnly.Remove("monitor");
        keptRamOnly.Remove("hdd");
        Assert.True(Length(keptRamOnly) > BuildCode.MaxLength);       // 前提: モニターと HDD を除いても長すぎる
        keptRamOnly.Set("ram", keptRamOnly["ram"].Take(1));
        Assert.True(Length(keptRamOnly) <= BuildCode.MaxLength);      // 前提: メモリを1種類にすれば収まる

        var r = BuildCode.Make(c);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
        Assert.Empty(r.Config["monitor"]);
        Assert.Empty(r.Config["hdd"]);
        Assert.Single(r.Config["ram"]);
        Assert.Equal(8, r.Config["gpu"].Count);
        Assert.Equal(8, r.Config["ssd"].Count);
        Assert.Equal(3, r.Notes.Count);
        Assert.Contains("モニター", r.Notes[0]);
        Assert.Contains("HDD", r.Notes[1]);
        Assert.Contains("メモリ", r.Notes[2]);
    }

    [Fact]
    public void A_normal_machine_is_far_below_the_limit()
    {
        var c = Big(cpus: 1, monitors: 3, hdds: 2, rams: 2);
        var r = BuildCode.Make(c);
        Assert.Empty(r.Notes);
        Assert.Equal(3, r.Config["monitor"].Count);
    }
}

public class BuildRuleTests
{
    // --- シリアル番号の網 ---

    [Theory]
    [InlineData("E823_8FA6_BF53_0001_001B_448B_4A5E_2D11.")]   // NVMe のシリアル（16 進と区切り）
    [InlineData("2020202057442D5743433446")]                   // 16 進に直されたシリアル（Win32_DiskDrive）
    [InlineData("4C4C4544-0035-4410-8044-B4C04F4E3732")]       // UUID
    [InlineData("S4EWNX0R12345678901")]                        // 長い数字の連なり
    [InlineData("00000000000000000000")]
    public void Serial_like_strings_are_detected(string s) => Assert.True(BuildRules.LooksLikeSerial(s));

    [Theory]
    [InlineData("ST2000DM008-2FR102")]           // Seagate の型番（空白なしで 18 文字）
    [InlineData("CMK64GX5M2B5600Z40")]           // Corsair のメモリの型番（空白なしで 18 文字）
    [InlineData("F4-3200C16-16GVK")]
    [InlineData("Samsung SSD 980 PRO 1TB")]
    [InlineData("WDC WD20EZAZ-00GGJB0")]
    [InlineData("TOSHIBA MG04ACA200E")]
    [InlineData("S5GXNF0R123456A")]              // 16 文字以下は網にかけない（型番と見分けられない）
    public void Model_names_are_not_mistaken_for_serials(string s) => Assert.False(BuildRules.LooksLikeSerial(s));

    [Fact]
    public void A_part_whose_name_looks_like_a_serial_is_dropped()
    {
        var c = new BuildConfig();
        c.Set("ssd", new[] { new BuildPart("E823_8FA6_BF53_0001_001B_448B_4A5E_2D11."), new BuildPart("Corsair MP600 PRO LPX", Cap: 2000, Type: "nvme") });
        var n = BuildCode.Normalize(c);
        Assert.Equal("Corsair MP600 PRO LPX", Assert.Single(n["ssd"]).Name);
    }

    [Fact]
    public void Ram_with_a_serial_or_empty_part_number_gets_a_generic_name()
    {
        var parts = BuildRules.Ram(new[]
        {
            new RawMemory("4C4C4544003544108044B4C04F4E3732", "80CE", 17179869184, 3200, 26, 0),
            new RawMemory("   ", "Unknown", 8589934592, 2666, 0, 24),
            new RawMemory("", "", 0, 0, 0, 0),   // 容量も無い → 出さない
        });
        Assert.Equal(2, parts.Count);
        Assert.Equal(new BuildPart("DDR4 16GB", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "Samsung"), parts[0]);
        Assert.Equal(new BuildPart("DDR3 8GB", Cap: 8, Speed: 2666, Type: "DDR3"), parts[1]);
    }

    // --- メモリ ---

    [Theory]
    [InlineData(26, 0, "DDR4")]
    [InlineData(34, 0, "DDR5")]
    [InlineData(35, 0, "LPDDR5")]
    [InlineData(30, 0, "LPDDR4")]
    [InlineData(24, 0, "DDR3")]
    [InlineData(0, 24, "DDR3")]     // SMBIOSMemoryType が無い古い機械は MemoryType
    [InlineData(2, 21, "DDR2")]     // 2 = Unknown
    [InlineData(0, 0, null)]
    [InlineData(99, 0, null)]
    public void Ram_type_mapping(int smbios, int memoryType, string? expected) =>
        Assert.Equal(expected, BuildRules.RamType(smbios, memoryType));

    [Theory]
    [InlineData("Corsair", "Corsair")]
    [InlineData("80CE", "Samsung")]
    [InlineData("CE00000000000000", "Samsung")]
    [InlineData("9E02000000000000", "Corsair")]
    [InlineData("04CD", "G.Skill")]
    [InlineData("ABCD", null)]       // 知らない番号は書かない
    [InlineData("Unknown", null)]
    [InlineData("", null)]
    public void Ram_manufacturer(string raw, string? expected) => Assert.Equal(expected, BuildRules.RamManufacturer(raw));

    [Theory]
    [InlineData(4800u, 5600u, 5600)]   // XMP/EXPO で定格より速く動いている → 動いている速さ
    [InlineData(3200u, 0u, 3200)]      // ConfiguredClockSpeed が読めない → 定格
    [InlineData(3200u, 2133u, 3200)]   // 定格より遅く動いている → 定格
    [InlineData(3200u, 3200u, 3200)]
    [InlineData(0u, 2666u, 2666)]      // 定格が報告されない
    [InlineData(0u, 0u, null)]
    public void Ram_speed_prefers_configured_only_when_faster(uint rated, uint configured, int? expected) =>
        Assert.Equal(expected, BuildRules.RamSpeed(rated, configured));

    [Fact]
    public void Xmp_kit_reports_its_running_speed()
    {
        // 実機（2026-10-11）: CMK64GX5M2B5600Z40 は Speed=4800・ConfiguredClockSpeed=5600
        var p = Assert.Single(BuildRules.Ram(new[] { new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368, 4800, 34, 0, 5600) }));
        Assert.Equal(5600, p.Speed);
    }

    [Fact]
    public void Ram_module_maps_capacity_speed_type_and_maker()
    {
        var p = Assert.Single(BuildRules.Ram(new[] { new RawMemory(" CMK64GX5M2B5600Z40 ", "Corsair", 34359738368, 4800, 34, 0) }));
        Assert.Equal(new BuildPart("CMK64GX5M2B5600Z40", Cap: 32, Speed: 4800, Type: "DDR5", Mfr: "Corsair"), p);
    }

    // --- 記憶装置 ---

    [Theory]
    [InlineData(17, "nvme")]
    [InlineData(11, "sata")]
    [InlineData(3, "sata")]
    [InlineData(10, "sas")]
    [InlineData(1, null)]
    [InlineData(8, null)]
    public void Storage_interface_mapping(int bus, string? expected) => Assert.Equal(expected, BuildRules.StorageInterface(bus));

    [Theory]
    [InlineData(1024209543168UL, 1000)]   // 「1TB」と売られている 1.024 TB
    [InlineData(512110190592UL, 512)]
    [InlineData(2000398934016UL, 2000)]
    [InlineData(2000396321280UL, 2000)]   // Win32_DiskDrive は少し小さく返す
    [InlineData(250059350016UL, 250)]
    [InlineData(4000787030016UL, 4000)]
    [InlineData(180012345678UL, 180)]     // 売られている容量から遠ければ、そのまま丸める
    public void Storage_capacity_is_decimal_gb_snapped_to_market_sizes(ulong bytes, int gb) =>
        Assert.Equal(gb, BuildRules.StorageGb(bytes));

    [Fact]
    public void Disks_split_into_ssd_and_hdd_and_skip_usb_and_virtual()
    {
        // 実機（2026-10-11）の MSFT_PhysicalDisk に、仮想ディスクと記憶域スペースを足した物
        var (ssd, hdd) = BuildRules.Disks(new[]
        {
            new RawDisk("PCIe SSD", 17, 4, 512110190592),
            new RawDisk("CSSD-S6L1TMGAX 1TB", 11, 4, 1024209543168),
            new RawDisk("TOSHIBA MG04ACA200E", 11, 3, 2000398934016),
            new RawDisk("SanDisk Cruzer Blade", 7, 0, 16005464064),     // USB メモリ
            new RawDisk("Corsair MP600 PRO LPX", 17, 4, 2000398934016),
            new RawDisk("Msft Virtual Disk", 14, 0, 137438953472),
            new RawDisk("Storage pool", 16, 0, 4000787030016),
            new RawDisk("Samsung SSD 870 EVO 1TB", 11, 0, 1000204886016), // MediaType 不明でも名前で SSD
            new RawDisk("WDC WD40EZAZ-00SF3B0", 11, 0, 4000787030016),    // 不明で名前にも手がかりが無ければ HDD
            new RawDisk("", 17, 4, 1000204886016),
        });
        Assert.Equal(new[] { "PCIe SSD", "CSSD-S6L1TMGAX 1TB", "Corsair MP600 PRO LPX", "Samsung SSD 870 EVO 1TB" }, ssd.Select(x => x.Name).ToArray());
        Assert.Equal(new[] { "TOSHIBA MG04ACA200E", "WDC WD40EZAZ-00SF3B0" }, hdd.Select(x => x.Name).ToArray());
        Assert.Equal(new BuildPart("CSSD-S6L1TMGAX 1TB", Cap: 1000, Type: "sata"), ssd[1]);
        Assert.Equal(new BuildPart("PCIe SSD", Cap: 512, Type: "nvme"), ssd[0]);
    }

    [Theory]
    [InlineData("IDE", @"IDE\DISKTOSHIBA_MG04ACA200E\1", "Fixed hard disk media", 11)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_NVME&PROD_CORSAIR_MP600\5&1", "Fixed hard disk media", 17)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_&PROD_PCIE_SSD\5&2", "Fixed hard disk media", 1)]
    [InlineData("USB", @"USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_BLADE\4C53", "Removable Media", 7)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_WD&PROD_ELEMENTS\1", "External hard disk media", 7)]
    public void Win32_diskdrive_fallback_maps_to_bus_types(string iface, string pnp, string media, int bus) =>
        Assert.Equal(bus, BuildRules.BusFromWin32(iface, pnp, media));

    // --- GPU・モニター・光学ドライブ ---

    [Fact]
    public void Virtual_and_integrated_display_adapters_are_not_gpus()
    {
        var gpus = BuildRules.Gpus(new[]
        {
            new RawGpu("NVIDIA GeForce RTX 5070", 12_817_793_024, true),
            new RawGpu("NVIDIA GeForce RTX 3080 Ti", 12_621_709_312, true),
            new RawGpu("AMD Radeon(TM) Graphics", 536_870_912, true),            // Ryzen 7000 の内蔵
            new RawGpu("Intel(R) UHD Graphics 770", 134_217_728, true),
            new RawGpu("AMD Radeon 780M Graphics", 536_870_912, true),
            new RawGpu("Intel(R) Arc(TM) Graphics", 134_217_728, true),
            new RawGpu("Intel(R) Arc(TM) A770 Graphics", 17_045_651_456, true),  // 単体の板は残す
            new RawGpu("AMD Radeon RX 7900 XTX", 25_753_026_560, true),
            new RawGpu("Microsoft Basic Display Adapter", 0, true),
            new RawGpu("Microsoft Basic Render Driver", 0, false),
            new RawGpu("Meta Virtual Monitor", 0, false),
            new RawGpu("Virtual Desktop Monitor", 0, false),
            new RawGpu("Microsoft Hyper-V Video", 0, true),
            new RawGpu("Parsec Virtual Display Adapter", 0, true),
            new RawGpu("NVIDIA GeForce RTX 5070", 12_817_793_024, false),          // 番地の無い別名
        });
        Assert.Equal(new[] { "NVIDIA GeForce RTX 5070", "NVIDIA GeForce RTX 3080 Ti", "Intel(R) Arc(TM) A770 Graphics", "AMD Radeon RX 7900 XTX" },
                     gpus.Select(g => g.Name).ToArray());
        Assert.Equal(new int?[] { 12, 12, 16, 24 }, gpus.Select(g => g.Vram).ToArray());
    }

    [Fact]
    public void Gpu_without_known_vram_has_no_vram_field()
    {
        var g = Assert.Single(BuildRules.Gpus(new[] { new RawGpu("NVIDIA GeForce GTX 1060 6GB", 0, true) }));
        Assert.Null(g.Vram);
    }

    [Fact]
    public void Monitor_names_are_decoded_and_empty_inactive_or_virtual_ones_skipped()
    {
        ushort[] raw = "PHL 278E1".Select(ch => (ushort)ch).Concat(new ushort[] { 0, 0, 0 }).ToArray();
        Assert.Equal("PHL 278E1", BuildRules.DecodeWmiString(raw));
        Assert.Equal("", BuildRules.DecodeWmiString(new ushort[] { 0, 0 }));
        Assert.Equal("", BuildRules.DecodeWmiString(null));

        var m = BuildRules.Monitors(new[]
        {
            new RawMonitor("UG27PJ", true), new RawMonitor("MFG27F4Q", true), new RawMonitor("PHL 278E1", true),
            new RawMonitor("", true), new RawMonitor("OLD MONITOR", false), new RawMonitor("Virtual Display", true),
        });
        Assert.Equal(new[] { "UG27PJ", "MFG27F4Q", "PHL 278E1" }, m.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Virtual_and_usb_optical_drives_are_skipped()
    {
        var o = BuildRules.Opticals(new[]
        {
            new RawOptical("HL-DT-ST BD-RE WH16NS60", @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&1"),
            new RawOptical("Msft Virtual DVD-ROM", @"SCSI\CDROM&VEN_MSFT&PROD_VIRTUAL_DVD-ROM\2&1"),
            new RawOptical("ASUS SDRW-08U9M-U", @"USBSTOR\CDROM&VEN_ASUS\1"),
        });
        Assert.Equal("HL-DT-ST BD-RE WH16NS60", Assert.Single(o).Name);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "TUF GAMING X670E-PLUS WIFI", "ASUSTeK COMPUTER INC. TUF GAMING X670E-PLUS WIFI", "ASUSTeK COMPUTER INC.")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI (MS-7D75)", "Micro-Star International Co., Ltd. MAG B650 TOMAHAWK WIFI (MS-7D75)", "Micro-Star International Co., Ltd.")]
    [InlineData("To be filled by O.E.M.", "B450M DS3H", "B450M DS3H", null)]
    public void Motherboard_name_is_maker_plus_product(string mfr, string product, string name, string? maker)
    {
        var p = Assert.Single(BuildRules.Motherboard(mfr, product));
        Assert.Equal(name, p.Name);
        Assert.Equal(maker, p.Mfr);
    }

    [Fact]
    public void Placeholder_board_product_yields_no_motherboard()
    {
        Assert.Empty(BuildRules.Motherboard("ASUSTeK COMPUTER INC.", "System Product Name"));
        Assert.Empty(BuildRules.Motherboard("Default string", "Default string"));
        Assert.Empty(BuildRules.Motherboard(null, null));
    }
}

public class BuildCollectorTests
{
    private static readonly RawMemory Stick = new("F4-3200C16-16GVK", "G Skill Intl", 17179869184, 3200, 26, 0);

    [Fact]
    public void A_failing_query_leaves_its_category_empty_instead_of_throwing()
    {
        var src = new BuildSources
        {
            Cpus = () => new[] { "AMD Ryzen 7 5800X 8-Core Processor" },
            Board = () => throw new System.Management.ManagementException("Access denied"),
            Memory = () => new[] { Stick, Stick },
            Gpus = () => throw new DllNotFoundException("dxgi.dll"),
            Disks = () => throw new UnauthorizedAccessException(),
            Monitors = () => throw new System.Runtime.InteropServices.COMException("WmiMonitorID"),
            Opticals = () => throw new InvalidOperationException("anything"),
        };
        var c = BuildCollector.Collect(src);
        Assert.Single(c["cpu"]);
        Assert.Equal(2, c["ram"].Count);
        foreach (var cat in new[] { "motherboard", "gpu", "ssd", "hdd", "monitor", "optical-drive" })
            Assert.Empty(c[cat]);

        var r = BuildCode.Make(c);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{"cpu":[{"n":"AMD Ryzen 7 5800X 8-Core Processor"}],"ram":[{"n":"F4-3200C16-16GVK","cap":16,"spd":3200,"t":"DDR4","mfr":"G Skill Intl","q":2}]}}""",
                     BuildCode.Decode(r.Code));
    }

    [Fact]
    public void Nothing_readable_gives_an_empty_but_valid_code()
    {
        var c = BuildCollector.Collect(new BuildSources());   // 既定の読む先は全部空
        Assert.True(c.IsEmpty);
        var r = BuildCode.Make(c);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{}}""", BuildCode.Decode(r.Code));
    }

    [Fact]
    public void Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BuildCollector.Collect(new BuildSources(), cts.Token));
    }

    [Fact]
    public void Summary_counts_modules_by_q()
    {
        var c = new BuildConfig();
        c.Set("cpu", new[] { new BuildPart("CPU") });
        c.Set("gpu", new[] { new BuildPart("GPU") });
        c.Set("motherboard", new[] { new BuildPart("MB") });
        c.Set("ram", new[] { new BuildPart("RAM", Q: 2) });
        c.Set("ssd", new[] { new BuildPart("SSD") });
        c.Set("hdd", new[] { new BuildPart("HDD") });
        c.Set("monitor", new[] { new BuildPart("MON") });
        Assert.Equal("CPU 1・GPU 1・マザーボード 1・メモリ 2枚・SSD 1・HDD 1・モニター 1", PartsLife.CheckWindow.T.BuildSummary(c));
    }
}

/// <summary>
/// この機械で本当に読む（読むだけ。管理者は要らない）。
/// 中身は `dotnet test --filter BuildCodeRealMachineTests --logger "console;verbosity=detailed"` で見られる。
/// </summary>
public class BuildCodeRealMachineTests
{
    private readonly ITestOutputHelper _out;
    public BuildCodeRealMachineTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void This_machine_produces_a_valid_code_without_serials_or_names()
    {
        var r = BuildCode.Make(BuildCollector.Collect());
        _out.WriteLine(r.Json);
        _out.WriteLine(r.Code);
        foreach (var n in r.Notes) _out.WriteLine("note: " + n);

        Assert.StartsWith(BuildCode.Prefix, r.Code);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
        string json = BuildCode.Decode(r.Code);
        using var doc = JsonDocument.Parse(json);
        Assert.Single(doc.RootElement.GetProperty("p").EnumerateObject(), p => p.Name == "cpu");   // CPU が読めない機械は無い
        foreach (var cat in doc.RootElement.GetProperty("p").EnumerateObject())
            foreach (var part in cat.Value.EnumerateArray())
                Assert.False(BuildRules.LooksLikeSerial(part.GetProperty("n").GetString()), part.ToString());
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\b[A-Z]:\\", json);   // ドライブ文字・パス
    }
}
