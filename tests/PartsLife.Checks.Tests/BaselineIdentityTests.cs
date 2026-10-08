using System.Buffers.Binary;
using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 基準値の鍵。**別の台の記録と比べて「増えています」と言わない**ことを確かめる。
/// </summary>
public class BaselineIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "baseline-id-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(9));

    private static DiskReport Nvme(string serial, ulong mediaErrors, string location = "", string model = "Test NVMe",
                                   long size = 2_000_398_934_016, string uniqueId = "") => new()
    {
        Number = 0, Model = model, Serial = serial, Bus = BusType.Nvme, Protocol = "nvme", SizeBytes = size,
        Location = location, UniqueId = uniqueId, NvmeHealthPage = Fixtures.FailingNvme(mediaErrors),
    };

    private static Finding Media(DiskJudgement j) => j.Findings.Single(f => f.Title == "メディアエラー");

    [Fact]
    public void Single_disk_growth_is_still_detected()
    {
        var store = new BaselineStore(_dir);
        var first = store.RecordAll(new[] { Nvme("S7KH123", 363_278) }, T0);
        Assert.Equal(BaselineState.First, first[0].State);

        var again = Nvme("S7KH123", 363_301);
        var o = store.RecordAll(new[] { again }, T0.AddDays(8))[0];
        Assert.Equal(BaselineState.Compared, o.State);
        var j = StorageJudge.Judge(again, o.Previous, o.State);
        Assert.Equal(Verdict.Check, Media(j).Verdict);
        Assert.Contains("(+23)", Media(j).Reason);
    }

    [Fact]
    public void Two_disks_with_the_same_serial_are_separated_by_location()
    {
        var store = new BaselineStore(_dir);
        // A は健康（0）、B は過去の記録が大きい。同じシリアル・型番・容量
        var a1 = Nvme("DUPSERIAL", 0, location: "0:0:0:0");
        var b1 = Nvme("DUPSERIAL", 363_278, location: "1:0:0:0");
        var first = store.RecordAll(new[] { a1, b1 }, T0);
        Assert.All(first, o => Assert.Equal(BaselineState.First, o.State));

        var a2 = Nvme("DUPSERIAL", 0, location: "0:0:0:0");
        var b2 = Nvme("DUPSERIAL", 363_278, location: "1:0:0:0");
        var second = store.RecordAll(new[] { a2, b2 }, T0.AddDays(1));
        Assert.Equal(BaselineState.Compared, second[0].State);
        Assert.Equal(0UL, second[0].Previous!.Counters[Counters.MediaErrors]);
        Assert.Equal(363_278UL, second[1].Previous!.Counters[Counters.MediaErrors]);
        var ja = StorageJudge.Judge(a2, second[0].Previous, second[0].State);
        Assert.Equal(Verdict.Ok, Media(ja).Verdict);
        Assert.DoesNotContain("増えています", Media(ja).Reason);
    }

    [Fact]
    public void Same_serial_without_location_is_not_compared_even_later_alone()
    {
        var store = new BaselineStore(_dir);
        var r1 = store.RecordAll(new[] { Nvme("DUPSERIAL", 0), Nvme("DUPSERIAL", 363_278) }, T0);
        Assert.All(r1, o => Assert.Equal(BaselineState.Duplicate, o.State));
        var r2 = store.RecordAll(new[] { Nvme("DUPSERIAL", 0), Nvme("DUPSERIAL", 363_278) }, T0.AddDays(1));
        Assert.All(r2, o => { Assert.Equal(BaselineState.Duplicate, o.State); Assert.Null(o.Previous); });

        // 片方だけ繋いだ回も、重なったことのある鍵なので位置で引く（位置が無ければ比べない）
        var alone = store.RecordAll(new[] { Nvme("DUPSERIAL", 363_278) }, T0.AddDays(2));
        Assert.Equal(BaselineState.Duplicate, alone[0].State);

        var j = StorageJudge.Judge(Nvme("DUPSERIAL", 363_278), alone[0].Previous, alone[0].State);
        Assert.Contains("比べられない（同じ識別子の台が複数）", Media(j).Reason);
        Assert.NotEqual(Verdict.Check, Media(j).Verdict);
    }

    [Fact]
    public void Twin_seen_once_is_looked_up_by_location_when_alone()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[] { Nvme("DUPSERIAL", 0, "0:0:0:0"), Nvme("DUPSERIAL", 363_278, "1:0:0:0") }, T0);
        // B だけ別の位置に繋いだ: A の記録とも B の記録とも比べない（初回扱い）
        var o = store.RecordAll(new[] { Nvme("DUPSERIAL", 363_278, "2:0:0:0") }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.First, o.State);
        // A を元の位置に戻せば、A 自身の記録と比べる
        var back = store.RecordAll(new[] { Nvme("DUPSERIAL", 0, "0:0:0:0") }, T0.AddDays(2))[0];
        Assert.Equal(BaselineState.Compared, back.State);
        Assert.Equal(0UL, back.Previous!.Counters[Counters.MediaErrors]);
    }

    [Fact]
    public void No_serial_twins_are_never_compared_and_say_so()
    {
        var store = new BaselineStore(_dir);
        var twins = new[] { Nvme("", 0), Nvme("", 363_278) };
        store.RecordAll(twins, T0);
        var o = store.RecordAll(new[] { Nvme("", 0), Nvme("", 363_278) }, T0.AddDays(1));
        Assert.All(o, x => { Assert.Equal(BaselineState.NoIdentity, x.State); Assert.Null(x.Previous); });
        // 記録もしない（弱い鍵の履歴は、次に別の台を混ぜる元になる）
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "disk-*.json").Length > 0);

        var j = StorageJudge.Judge(Nvme("", 0), null, BaselineState.NoIdentity);
        Assert.Equal(Verdict.Ok, Media(j).Verdict);              // 0 件は 0 件
        var jb = StorageJudge.Judge(Nvme("", 363_278), null, BaselineState.NoIdentity);
        Assert.Contains("比べられない（識別子なし）", Media(jb).Reason);
        Assert.DoesNotContain("増えています", Media(jb).Reason);
        Assert.DoesNotContain("次回", Media(jb).Reason);
    }

    [Fact]
    public void Placeholder_serial_counts_as_no_serial_but_a_unique_id_is_enough()
    {
        Assert.False(BaselineIdentity.Of(Nvme("000000000000", 0)).Strong);
        Assert.False(BaselineIdentity.Of(Nvme("   ", 0)).Strong);
        Assert.True(BaselineIdentity.Of(Nvme("", 0, uniqueId: "eui.0025385891407a2b")).Strong);
        Assert.NotEqual(BaselineIdentity.Of(Nvme("X1", 0, uniqueId: "eui.01")).Key,
                        BaselineIdentity.Of(Nvme("X1", 0, uniqueId: "eui.02")).Key);
    }

    [Fact]
    public void Serials_that_SafeName_collapses_get_separate_files_and_histories()
    {
        var store = new BaselineStore(_dir);
        var a = Nvme("AB/CD 12", 0);
        var b = Nvme("AB CD/12", 363_278);
        Assert.Equal(BaselineStore.SafeName(a.Serial), BaselineStore.SafeName(b.Serial));
        Assert.NotEqual(store.PathFor(BaselineIdentity.Of(a)), store.PathFor(BaselineIdentity.Of(b)));
        Assert.StartsWith("disk-AB_CD_12-", BaselineIdentity.Of(a).FileName);

        store.RecordAll(new[] { a, b }, T0);
        var o = store.RecordAll(new[] { Nvme("AB/CD 12", 0), Nvme("AB CD/12", 363_278) }, T0.AddDays(1));
        Assert.Equal(0UL, o[0].Previous!.Counters[Counters.MediaErrors]);
        Assert.Equal(363_278UL, o[1].Previous!.Counters[Counters.MediaErrors]);
    }

    [Fact]
    public void Model_size_or_bus_difference_makes_a_different_disk()
    {
        var k = BaselineIdentity.Of(Nvme("SAME", 0)).Key;
        Assert.NotEqual(k, BaselineIdentity.Of(Nvme("SAME", 0, model: "Other")).Key);
        Assert.NotEqual(k, BaselineIdentity.Of(Nvme("SAME", 0, size: 1)).Key);
        var sata = Nvme("SAME", 0);
        sata.Bus = BusType.Sata;
        Assert.NotEqual(k, BaselineIdentity.Of(sata).Key);
        // 区切り文字を値に混ぜても、別の鍵と同じにならない
        Assert.NotEqual(BaselineIdentity.Of(Nvme("A|uid=B", 0)).Key, BaselineIdentity.Of(Nvme("A", 0, uniqueId: "B")).Key);
    }

    [Fact]
    public void Power_on_hours_going_backwards_withholds_the_comparison()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[] { Nvme("POH1", 0) }, T0);
        var later = Nvme("POH1", 363_278);
        BinaryPrimitives.WriteUInt64LittleEndian(later.NvmeHealthPage.AsSpan(128), 1);   // Power On Hours
        var o = store.RecordAll(new[] { later }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.Mismatch, o.State);
        Assert.Null(o.Previous);
        Assert.NotEqual(Verdict.Check, Media(StorageJudge.Judge(later, o.Previous, o.State)).Verdict);
    }

    [Fact]
    public void Legacy_file_is_migrated_only_when_it_is_safe()
    {
        Directory.CreateDirectory(_dir);
        var store = new BaselineStore(_dir);
        void WriteLegacy(string serial, string model, ulong media)
        {
            File.WriteAllText(store.LegacyPathFor(serial),
                $$$"""{"key":"{{{serial}}}","model":"{{{model}}}","entries":[{"at":"2026-09-01T00:00:00+09:00","counters":{"nvme.media_errors":{{{media}}}}}]}""");
        }
        WriteLegacy("MIG1", "Test NVMe", 100);
        WriteLegacy("DUP1", "Test NVMe", 100);
        WriteLegacy("OTHERMODEL", "Some Other Disk", 100);

        var o = store.RecordAll(new[]
        {
            Nvme("MIG1", 100),
            Nvme("DUP1", 100, "0:0:0:0"), Nvme("DUP1", 0, "1:0:0:0"),   // 今回2台ある → 移さない
            Nvme("OTHERMODEL", 100),                                     // 型番が違う → 移さない
        }, T0);
        Assert.Equal(BaselineState.Compared, o[0].State);
        Assert.Equal(100UL, o[0].Previous!.Counters[Counters.MediaErrors]);
        Assert.True(File.Exists(store.LegacyPathFor("MIG1") + ".migrated"));
        Assert.False(File.Exists(store.LegacyPathFor("MIG1")));

        Assert.Equal(BaselineState.First, o[1].State);
        Assert.Equal(BaselineState.First, o[2].State);
        Assert.True(File.Exists(store.LegacyPathFor("DUP1")));
        Assert.Equal(BaselineState.First, o[3].State);
        Assert.True(File.Exists(store.LegacyPathFor("OTHERMODEL")));
    }

    [Fact]
    public void Unavailable_disks_are_not_recorded_but_still_count_as_twins()
    {
        var store = new BaselineStore(_dir);
        var broken = Nvme("DUPSERIAL", 0);
        broken.Unavailable = "could not read";
        var o = store.RecordAll(new[] { broken, Nvme("DUPSERIAL", 5) }, T0);
        Assert.Equal(BaselineState.NotRecorded, o[0].State);
        Assert.Equal(BaselineState.Duplicate, o[1].State);
    }
}

public class DeviceIdParsingTests
{
    /// <summary>STORAGE_DEVICE_ID_DESCRIPTOR を仕様の位置で組む。</summary>
    private static byte[] Descriptor(params (int CodeSet, int Type, int Assoc, byte[] Value)[] ids)
    {
        var body = new List<byte>();
        foreach (var (cs, type, assoc, value) in ids)
        {
            int len = 16 + value.Length;
            int next = (len + 3) & ~3;
            var e = new byte[next];
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0), cs);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), (ushort)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), (ushort)next);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(12), assoc);
            value.CopyTo(e, 16);
            body.AddRange(e);
        }
        var d = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)d.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), (uint)ids.Length);
        body.CopyTo(d, 12);
        return d;
    }

    [Fact]
    public void Eui64_is_preferred_over_naa_and_port_identifiers_are_ignored()
    {
        var d = Descriptor(
            (1, 3, 1, new byte[] { 0x50, 1, 2, 3, 4, 5, 6, 7 }),            // NAA だがポートの物
            (1, 3, 0, new byte[] { 0x50, 0, 0x39, 1, 2, 3, 4, 5 }),        // 機器の NAA
            (1, 2, 0, new byte[] { 0x00, 0x25, 0x38, 0x58, 0x91, 0x40, 0x7A, 0x2B }));
        Assert.Equal("eui.0025385891407a2b", StorageProtocol.ParseDeviceUniqueId(d));
    }

    [Fact]
    public void Naa_is_used_when_there_is_no_eui()
    {
        var d = Descriptor((2, 8, 0, "ascii"u8.ToArray()), (1, 3, 0, new byte[] { 0x50, 0, 0x39, 1, 2, 3, 4, 5 }));
        Assert.Equal("naa.5000390102030405", StorageProtocol.ParseDeviceUniqueId(d));
    }

    [Fact]
    public void Zero_or_ff_identifiers_and_broken_descriptors_give_nothing()
    {
        Assert.Equal("", StorageProtocol.ParseDeviceUniqueId(Descriptor((1, 2, 0, new byte[8]))));
        Assert.Equal("", StorageProtocol.ParseDeviceUniqueId(Descriptor((1, 2, 0, Enumerable.Repeat((byte)0xFF, 16).ToArray()))));
        Assert.Equal("", StorageProtocol.ParseDeviceUniqueId(new byte[8]));
        var lying = Descriptor((1, 2, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        BinaryPrimitives.WriteUInt16LittleEndian(lying.AsSpan(12 + 8), 4000);    // 中身より長いと言い張る
        Assert.Equal("", StorageProtocol.ParseDeviceUniqueId(lying));
    }

    [Fact]
    public void Scsi_address_is_port_path_target_lun()
    {
        Assert.Equal("1:0:2:0", StorageProtocol.ParseScsiAddress(new byte[] { 8, 0, 0, 0, 1, 0, 2, 0 }));
        Assert.Equal("", StorageProtocol.ParseScsiAddress(new byte[] { 0, 0, 0, 0, 1, 0, 2, 0 }));
    }
}
