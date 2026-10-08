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

    /// <summary>通電時間（NVMe 2.0 Figure 207: 128）と書き込み量（48）を書き換える。</summary>
    private static DiskReport With(DiskReport r, ulong powerOnHours, ulong? dataUnitsWritten = null)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(r.NvmeHealthPage.AsSpan(128), powerOnHours);
        if (dataUnitsWritten is { } w) BinaryPrimitives.WriteUInt64LittleEndian(r.NvmeHealthPage.AsSpan(48), w);
        return r;
    }

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

    /// <summary>今回の値が記録されたか（位置付きの鍵のファイルの最後の記録）。</summary>
    private static ulong? LastRecordedMedia(BaselineStore store, DiskReport r)
    {
        var h = store.Load(BaselineIdentity.Of(r, withLocation: true));
        return h.Entries.Count > 0 ? h.Entries[^1].Counters[Counters.MediaErrors] : null;
    }

    [Fact]
    public void Two_disks_with_the_same_serial_are_recorded_by_location_but_never_compared()
    {
        var store = new BaselineStore(_dir);
        // A は健康（0）、B は過去の記録が大きい。同じシリアル・型番・容量
        var a1 = Nvme("DUPSERIAL", 0, location: "0:0:0:0");
        var b1 = Nvme("DUPSERIAL", 363_278, location: "1:0:0:0");
        var first = store.RecordAll(new[] { a1, b1 }, T0);
        Assert.All(first, o => { Assert.Equal(BaselineState.Duplicate, o.State); Assert.Null(o.Previous); });

        var a2 = Nvme("DUPSERIAL", 0, location: "0:0:0:0");
        var b2 = Nvme("DUPSERIAL", 363_301, location: "1:0:0:0");
        var second = store.RecordAll(new[] { a2, b2 }, T0.AddDays(1));
        Assert.All(second, o => { Assert.Equal(BaselineState.Duplicate, o.State); Assert.Null(o.Previous); });
        // 比べはしないが、今回の値は位置付きの鍵で残っている（黙って捨てない）
        Assert.Equal(0UL, LastRecordedMedia(store, a2));
        Assert.Equal(363_301UL, LastRecordedMedia(store, b2));
        Assert.Equal(2, store.Load(BaselineIdentity.Of(b2, withLocation: true)).Entries.Count);

        var jb = StorageJudge.Judge(b2, second[1].Previous, second[1].State);
        Assert.DoesNotContain("増えています", Media(jb).Reason);
        Assert.DoesNotContain("増えていません", Media(jb).Reason);
        Assert.Contains("比べられない（同じ識別子のディスクが複数あります）", Media(jb).Reason);
        Assert.Empty(jb.Deltas);
    }

    [Fact]
    public void Same_serial_without_location_is_not_compared_even_later_alone()
    {
        var store = new BaselineStore(_dir);
        var r1 = store.RecordAll(new[] { Nvme("DUPSERIAL", 0), Nvme("DUPSERIAL", 363_278) }, T0);
        Assert.All(r1, o => Assert.Equal(BaselineState.Duplicate, o.State));
        var r2 = store.RecordAll(new[] { Nvme("DUPSERIAL", 0), Nvme("DUPSERIAL", 363_278) }, T0.AddDays(1));
        Assert.All(r2, o => { Assert.Equal(BaselineState.Duplicate, o.State); Assert.Null(o.Previous); });

        // 片方だけ繋いだ回も、重なったことのある鍵なので比べない
        var alone = store.RecordAll(new[] { Nvme("DUPSERIAL", 363_278) }, T0.AddDays(2));
        Assert.Equal(BaselineState.Duplicate, alone[0].State);

        var j = StorageJudge.Judge(Nvme("DUPSERIAL", 363_278), alone[0].Previous, alone[0].State);
        Assert.Contains("比べられない（同じ識別子のディスクが複数あります）", Media(j).Reason);
        Assert.NotEqual(Verdict.Check, Media(j).Verdict);
    }

    [Fact]
    public void Twin_seen_once_is_never_compared_again_even_alone()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[] { Nvme("DUPSERIAL", 0, "0:0:0:0"), Nvme("DUPSERIAL", 363_278, "1:0:0:0") }, T0);
        // B だけ別の位置に繋いだ: 比べない（記録はする）
        var o = store.RecordAll(new[] { Nvme("DUPSERIAL", 363_278, "2:0:0:0") }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.Duplicate, o.State);
        Assert.Equal(363_278UL, LastRecordedMedia(store, Nvme("DUPSERIAL", 0, "2:0:0:0")));
        // A を元の位置に戻しても比べない（その位置にいるのが A だとは言えない）
        var back = store.RecordAll(new[] { Nvme("DUPSERIAL", 0, "0:0:0:0") }, T0.AddDays(2))[0];
        Assert.Equal(BaselineState.Duplicate, back.State);
        Assert.Null(back.Previous);
    }

    /// <summary>
    /// 一緒に通電されている双子（安い SSD・RAID の構成員）は通電時間がほぼ同じ。
    /// 位置を入れ替えると、減りも進み過ぎもしないので通電時間では見抜けず、SATA は書き込み量も記録しない。
    /// </summary>
    [Fact]
    public void Twins_with_nearly_equal_hours_that_swap_places_are_not_compared()
    {
        var store = new BaselineStore(_dir);
        // SATA の双子。A は代替待ち 0、B は 8。通電時間は 1 時間違い
        store.RecordAll(new[] { Sata("TWINSSD", 12_000, 0, 0, "0:0:0:0"), Sata("TWINSSD", 12_001, 0, 8, "1:0:0:0") }, T0);

        // 1 日後、ケーブルを差し替えた。A が B の元の位置、B が A の元の位置に来る
        var a = Sata("TWINSSD", 12_024, 0, 0, "1:0:0:0");
        var b = Sata("TWINSSD", 12_025, 0, 8, "0:0:0:0");
        var o = store.RecordAll(new[] { b, a }, T0.AddDays(1));
        Assert.All(o, x => { Assert.Equal(BaselineState.Duplicate, x.State); Assert.Null(x.Previous); });
        // 通電時間では見抜けないことの確認（これまでの確かめ方では「同じ台」に見えた）
        var aRecordAtBsNewPlace = new BaselineEntry { At = T0, Counters = Counters.From(Sata("TWINSSD", 12_000, 0, 0)) };
        Assert.Equal(BaselineStore.Continuity.Same, BaselineStore.CheckContinuity(aRecordAtBsNewPlace, Counters.From(b), T0.AddDays(1)));

        var pending = StorageJudge.Judge(b, o[0].Previous, o[0].State).Findings.Single(f => f.Title == "代替待ちセクタ");
        Assert.DoesNotContain("増えています", pending.Reason);
        Assert.DoesNotContain("増えていません", pending.Reason);
        Assert.Contains("比べられない（同じ識別子のディスクが複数あります）", pending.Reason);

        // 片方だけ、次の回に繋いでも同じ
        Assert.Equal(BaselineState.Duplicate, store.RecordAll(new[] { Sata("TWINSSD", 12_048, 0, 8, "0:0:0:0") }, T0.AddDays(2))[0].State);
        // NVMe の双子でも同じ（通電時間がほぼ同じなら進み過ぎにもならない）
        store.RecordAll(new[] { With(Nvme("TWINNV", 0, "0:0:0:0"), 5_000), With(Nvme("TWINNV", 363_278, "1:0:0:0"), 5_001) }, T0);
        var n = store.RecordAll(new[] { With(Nvme("TWINNV", 363_278, "0:0:0:0"), 5_025), With(Nvme("TWINNV", 0, "1:0:0:0"), 5_024) }, T0.AddDays(1));
        Assert.All(n, x => { Assert.Equal(BaselineState.Duplicate, x.State); Assert.Null(x.Previous); });
    }

    [Fact]
    public void Lone_disk_beside_twins_is_compared_as_before()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[]
        {
            Sata("TWINSSD", 12_000, 0, 0, "0:0:0:0"), Sata("TWINSSD", 12_001, 0, 8, "1:0:0:0"),
            Sata("LONEHDD", 30_000, 10, 8, "2:0:0:0"),
        }, T0);
        var lone = Sata("LONEHDD", 30_024, 12, 8, "3:0:0:0");      // 位置が変わっても、一人なら比べる
        var o = store.RecordAll(new[] { Sata("TWINSSD", 12_024, 0, 0, "0:0:0:0"), lone }, T0.AddDays(1));
        Assert.Equal(BaselineState.Duplicate, o[0].State);
        Assert.Equal(BaselineState.Compared, o[1].State);
        var j = StorageJudge.Judge(lone, o[1].Previous, o[1].State);
        var realloc = j.Findings.Single(f => f.Title == "代替処理済みセクタ");
        Assert.Equal(Verdict.Check, realloc.Verdict);
        Assert.Contains("(+2)", realloc.Reason);
        Assert.Contains("増えていません", j.Findings.Single(f => f.Title == "代替待ちセクタ").Reason);
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
    public void Swapped_same_serial_disks_are_not_compared_with_each_others_records()
    {
        var store = new BaselineStore(_dir);
        // 同じシリアル・一意な識別子なし。A は通電時間が長くメディアエラー 363,278、B は短く 0 件
        DiskReport A(string loc) => With(Nvme("DUPSERIAL", 363_278, loc), powerOnHours: 21_345);
        DiskReport B(string loc) => With(Nvme("DUPSERIAL", 0, loc), powerOnHours: 1_200, dataUnitsWritten: 900_000);
        store.RecordAll(new[] { A("0:0:0:0"), B("1:0:0:0") }, T0);

        // ケーブルを差し替えた（番号の振り直しでも同じ）。A が B の元の位置に来る
        var a = A("1:0:0:0");
        var o = store.RecordAll(new[] { B("0:0:0:0"), a }, T0.AddDays(1));
        Assert.All(o, x => { Assert.Equal(BaselineState.Duplicate, x.State); Assert.Null(x.Previous); });

        var j = StorageJudge.Judge(a, o[1].Previous, o[1].State);
        Assert.NotEqual(Verdict.Check, Media(j).Verdict);
        Assert.DoesNotContain("増えています", Media(j).Reason);
        Assert.DoesNotContain("+363,278", Media(j).Reason);
        Assert.Contains("比べられない（同じ識別子のディスクが複数あります）", Media(j).Reason);
        Assert.Empty(j.Deltas);

        // 位置がそのままでも、双子の鍵とは以後ずっと比べない
        var again = store.RecordAll(new[] { B("0:0:0:0"), A("1:0:0:0") }, T0.AddDays(2));
        Assert.All(again, x => { Assert.Equal(BaselineState.Duplicate, x.State); Assert.Null(x.Previous); });
    }

    [Fact]
    public void Same_serial_replacement_unit_is_not_compared_with_the_old_units_record()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[] { With(Nvme("REPL1", 0), powerOnHours: 1_000) }, T0);
        // 3 日後、同じシリアルを返す別の台（通電時間が長く、エラーの多い中古品）に替わった
        var replacement = With(Nvme("REPL1", 363_278), powerOnHours: 21_345);
        var o = store.RecordAll(new[] { replacement }, T0.AddDays(3))[0];
        Assert.Equal(BaselineState.AheadOfClock, o.State);
        var j = StorageJudge.Judge(replacement, o.Previous, o.State);
        Assert.NotEqual(Verdict.Check, Media(j).Verdict);
        Assert.DoesNotContain("増えています", Media(j).Reason);
        Assert.Contains("比べられない", Media(j).Reason);

        // 書き込み量が減った（通電時間は辻褄が合う）だけでも、別の台として扱う
        var store2 = new BaselineStore(Path.Combine(_dir, "w"));
        store2.RecordAll(new[] { With(Nvme("REPL2", 0), powerOnHours: 1_000, dataUnitsWritten: 5_000_000) }, T0);
        var other = With(Nvme("REPL2", 363_278), powerOnHours: 1_010, dataUnitsWritten: 4_000_000);
        Assert.Equal(BaselineState.Mismatch, store2.RecordAll(new[] { other }, T0.AddDays(1))[0].State);
    }

    [Fact]
    public void Genuine_growth_with_hours_that_fit_the_clock_is_still_detected()
    {
        var store = new BaselineStore(_dir);
        store.RecordAll(new[] { With(Nvme("REAL1", 363_278), powerOnHours: 21_345, dataUnitsWritten: 48_000_000) }, T0);
        // 8 日（192 時間）で 150 時間ついていた。書き込みも増えた
        var later = With(Nvme("REAL1", 363_301), powerOnHours: 21_495, dataUnitsWritten: 48_500_000);
        var o = store.RecordAll(new[] { later }, T0.AddDays(8))[0];
        Assert.Equal(BaselineState.Compared, o.State);
        var j = StorageJudge.Judge(later, o.Previous, o.State);
        Assert.Equal(Verdict.Check, Media(j).Verdict);
        Assert.Contains("(+23)", Media(j).Reason);
        Assert.Contains("増えています", Media(j).Reason);

        // 付けっぱなし（経った時間ぶんそのまま）でも、切り捨てのぶん 1 時間多くても比べる
        var on = With(Nvme("REAL1", 363_301), powerOnHours: 21_495 + 49);
        Assert.Equal(BaselineState.Compared, store.RecordAll(new[] { on }, T0.AddDays(10))[0].State);
    }

    [Fact]
    public void Hours_ahead_of_the_clock_allow_only_the_stated_slack()
    {
        var prev = new BaselineEntry { At = T0, Counters = new() { [Counters.PowerOnHours] = 100 } };
        Dictionary<string, ulong> Poh(ulong h) => new() { [Counters.PowerOnHours] = h };
        double slack = BaselineStore.HoursSlack.TotalHours + BaselineStore.ClockSkew.TotalHours;
        Assert.Equal(3, slack);
        Assert.Equal(BaselineStore.Continuity.Same, BaselineStore.CheckContinuity(prev, Poh(113), T0.AddHours(10)));
        Assert.Equal(BaselineStore.Continuity.AheadOfClock, BaselineStore.CheckContinuity(prev, Poh(114), T0.AddHours(10)));
        // 時計が戻っていても（前回より前の時刻）、余裕を超えて進んだら別の台
        Assert.Equal(BaselineStore.Continuity.Same, BaselineStore.CheckContinuity(prev, Poh(103), T0.AddHours(-5)));
        Assert.Equal(BaselineStore.Continuity.AheadOfClock, BaselineStore.CheckContinuity(prev, Poh(104), T0.AddHours(-5)));
        // ATA の通電時間（属性 9）は単位が機種まちまち（分・秒）なので、進み方では判断しない。減ったときだけ別の台
        var ata = new BaselineEntry { At = T0, Counters = new() { [Counters.Ata(9)] = 5_000 } };
        Assert.Equal(BaselineStore.Continuity.Same,
                     BaselineStore.CheckContinuity(ata, new Dictionary<string, ulong> { [Counters.Ata(9)] = 9_000 }, T0.AddDays(1)));
        Assert.Equal(BaselineStore.Continuity.Same,
                     BaselineStore.CheckContinuity(ata, new Dictionary<string, ulong> { [Counters.Ata(9)] = 5_020 }, T0.AddDays(1)));
        Assert.Equal(BaselineStore.Continuity.WentBackwards,
                     BaselineStore.CheckContinuity(ata, new Dictionary<string, ulong> { [Counters.Ata(9)] = 4_999 }, T0.AddDays(1)));
    }

    /// <summary>SATA の HDD（見本は <see cref="Fixtures.SataWithPendingSectors"/>）の生の値を、属性の番号で書き換える。</summary>
    private static DiskReport Sata(string serial, ulong powerOn, ulong reallocated, ulong pending, string location = "")
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        void Raw(byte id, ulong raw)
        {
            for (int off = 2; off + 12 <= 2 + 30 * 12; off += 12)
                if (d[off] == id) { for (int i = 0; i < 6; i++) d[off + 5 + i] = (byte)(raw >> (8 * i)); return; }
            throw new InvalidOperationException($"attribute {id} is not in the fixture");
        }
        Raw(9, powerOn);
        Raw(5, reallocated);
        Raw(197, pending);
        return new DiskReport
        {
            Model = "Old HDD", Serial = serial, Bus = BusType.Sata, Protocol = "ata", SizeBytes = 80_026_361_856,
            Location = location, AtaSmartData = d, AtaSmartThresholds = t,
        };
    }

    [Fact]
    public void Ata_power_on_counted_in_minutes_does_not_hide_real_growth()
    {
        var store = new BaselineStore(_dir);
        // 属性 9 を分で数える古い HDD。1 日（1,440 分）付けっぱなしなら 1,440 進む
        var first = store.RecordAll(new[] { Sata("MAXTOR1", 1_260_000, reallocated: 10, pending: 8) }, T0)[0];
        Assert.Equal(BaselineState.First, first.State);

        var later = Sata("MAXTOR1", 1_260_000 + 1_440, reallocated: 14, pending: 11);
        var o = store.RecordAll(new[] { later }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.Compared, o.State);
        var j = StorageJudge.Judge(later, o.Previous, o.State);
        var realloc = j.Findings.Single(f => f.Title == "代替処理済みセクタ");
        Assert.Equal(Verdict.Check, realloc.Verdict);
        Assert.Contains("(+4)", realloc.Reason);
        Assert.Contains("増えています", realloc.Reason);
        var pending = j.Findings.Single(f => f.Title == "代替待ちセクタ");
        Assert.Contains("(+3)", pending.Reason);
        Assert.Contains("増えています", pending.Reason);

        // 分で数える台の属性 9 が減ったら、単位に関係なくおかしい → 比べない
        var back = Sata("MAXTOR1", 1_000, reallocated: 99, pending: 99);
        Assert.Equal(BaselineState.Mismatch, store.RecordAll(new[] { back }, T0.AddDays(2))[0].State);

        // NVMe の通電時間（仕様で「時間」）は今まで通り: 同じシリアルの別の台に入れ替わったら比べない
        var nvme = new BaselineStore(Path.Combine(_dir, "nvme"));
        nvme.RecordAll(new[] { With(Nvme("SWAPN", 0), powerOnHours: 1_200) }, T0);
        var swapped = With(Nvme("SWAPN", 363_278), powerOnHours: 21_345);
        var s = nvme.RecordAll(new[] { swapped }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.AheadOfClock, s.State);
        Assert.Null(s.Previous);
        Assert.DoesNotContain("増えています", Media(StorageJudge.Judge(swapped, s.Previous, s.State)).Reason);
    }

    [Fact]
    public void Run_withheld_because_the_clock_went_back_does_not_hide_growth()
    {
        var store = new BaselineStore(_dir);
        var disk = BaselineIdentity.Of(Nvme("CLOCK1", 0));
        store.RecordAll(new[] { With(Nvme("CLOCK1", 0), powerOnHours: 1_000) }, T0);

        // 1 日付けっぱなしで 23 件増えた。ところが PC の時計が 30 日前に戻っている → 比べられない
        var o2 = store.RecordAll(new[] { With(Nvme("CLOCK1", 23), powerOnHours: 1_024) }, T0.AddDays(-30))[0];
        Assert.Equal(BaselineState.AheadOfClock, o2.State);
        Assert.True(store.Load(disk).Entries[^1].Withheld);
        Assert.Contains("\"withheld\": true", File.ReadAllText(store.PathFor(disk)));

        // 時計が直った次の回は、最後に普通に比べられた記録（1 回目）と比べる。2 回目と比べると「変わらない」になる
        var later = With(Nvme("CLOCK1", 23), powerOnHours: 1_048);
        var o3 = store.RecordAll(new[] { later }, T0.AddDays(2))[0];
        Assert.Equal(BaselineState.Compared, o3.State);
        Assert.Equal(T0, o3.Previous!.At);
        Assert.Equal(0UL, o3.Previous.Counters[Counters.MediaErrors]);
        var j = StorageJudge.Judge(later, o3.Previous, o3.State);
        Assert.Equal(Verdict.Check, Media(j).Verdict);
        Assert.Contains("(+23)", Media(j).Reason);
        Assert.Contains("増えています", Media(j).Reason);
        Assert.False(store.Load(disk).Entries[^1].Withheld);
        Assert.Equal(1, File.ReadAllText(store.PathFor(disk)).Split("\"withheld\"").Length - 1);   // 付けるのは 2 回目だけ

        // 別の台だと分かった回（時計は進んでいる）は飛ばさない: 次はその回の記録と比べる
        var swapped = With(Nvme("CLOCK1", 363_278), powerOnHours: 21_345);
        Assert.Equal(BaselineState.AheadOfClock, store.RecordAll(new[] { swapped }, T0.AddDays(3))[0].State);
        Assert.False(store.Load(disk).Entries[^1].Withheld);
        var o5 = store.RecordAll(new[] { With(Nvme("CLOCK1", 363_278), powerOnHours: 21_369) }, T0.AddDays(4))[0];
        Assert.Equal(BaselineState.Compared, o5.State);
        Assert.Equal(21_345UL, o5.Previous!.Counters[Counters.PowerOnHours]);
    }

    [Fact]
    public void Trimming_keeps_the_comparison_base_behind_withheld_runs()
    {
        var store = new BaselineStore(_dir);
        var disk = BaselineIdentity.Of(Nvme("CLOCK2", 0));
        store.RecordAll(new[] { With(Nvme("CLOCK2", 0), powerOnHours: 1_000) }, T0);
        // 時計が戻ったままの回が上限を超えて続いても、比べる相手（1 回目）は捨てない
        for (int i = 1; i <= BaselineStore.MaxEntries + 5; i++)
            Assert.Equal(BaselineState.AheadOfClock,
                         store.RecordAll(new[] { With(Nvme("CLOCK2", 7), powerOnHours: 1_000 + 10 + (ulong)i) }, T0.AddDays(-30))[0].State);
        var h = store.Load(disk);
        Assert.Equal(BaselineStore.MaxEntries, h.Entries.Count);
        Assert.Equal(T0, h.Entries[0].At);
        Assert.False(h.Entries[0].Withheld);
    }

    /// <summary>v1 のファイル（ファイル名 = シリアル）を、記録の並び（時刻・通電時間・メディアエラー）から書く。</summary>
    private static void WriteLegacy(BaselineStore store, string serial, string model, params (DateTimeOffset At, ulong? Poh, ulong Media)[] entries)
    {
        Directory.CreateDirectory(store.Directory);
        var list = string.Join(",", entries.Select(e =>
        {
            string poh = e.Poh is { } h ? $",\"nvme.power_on_hours\":{h}" : "";
            return $"{{\"at\":\"{e.At:O}\",\"counters\":{{\"nvme.media_errors\":{e.Media}{poh}}}}}";
        }));
        File.WriteAllText(store.LegacyPathFor(serial), $"{{\"key\":\"{serial}\",\"model\":\"{model}\",\"entries\":[{list}]}}");
    }

    [Fact]
    public void Legacy_history_mixing_two_same_serial_disks_is_not_migrated()
    {
        var store = new BaselineStore(_dir);
        var d = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(9));
        // 旧い版が、同じシリアルの2台（A: 21,345 時間・363,278 件 / B: 1,200 時間・0 件）を交互に書いていた
        WriteLegacy(store, "MIXED1", "Test NVMe",
            (d, 21_300, 363_278), (d.AddMinutes(1), 1_180, 0),
            (d.AddDays(7), 21_330, 363_278), (d.AddDays(7).AddMinutes(1), 1_190, 0));

        // 今回は A だけを繋いでいる。最後の記録は B の物 → 移さない
        var a = With(Nvme("MIXED1", 363_278), powerOnHours: 21_345);
        var o = store.RecordAll(new[] { a }, T0)[0];
        Assert.Equal(BaselineState.LegacyRefused, o.State);
        Assert.Null(o.Previous);
        var j = StorageJudge.Judge(a, o.Previous, o.State);
        Assert.NotEqual(Verdict.Check, Media(j).Verdict);
        Assert.Contains("比べられない（以前の記録", Media(j).Reason);
        Assert.DoesNotContain("増えています", Media(j).Reason);
        // 旧いファイルは消さず、改名もしない
        Assert.True(File.Exists(store.LegacyPathFor("MIXED1")));
        Assert.False(File.Exists(store.LegacyPathFor("MIXED1") + ".migrated"));

        // 次の回は、今回から始めた記録と比べる
        var next = store.RecordAll(new[] { With(Nvme("MIXED1", 363_278), powerOnHours: 21_369) }, T0.AddDays(1))[0];
        Assert.Equal(BaselineState.Compared, next.State);
        Assert.Equal(21_345UL, next.Previous!.Counters[Counters.PowerOnHours]);
        Assert.True(File.Exists(store.LegacyPathFor("MIXED1")));
    }

    [Fact]
    public void Legacy_history_is_not_migrated_when_hours_jump_or_cannot_be_checked()
    {
        var store = new BaselineStore(_dir);
        var d = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(9));
        // 隣り合う記録で、1 日に 20,000 時間進んでいる（B の後に A が来た）。減ってはいない
        WriteLegacy(store, "JUMP1", "Test NVMe", (d, 1_200, 0), (d.AddDays(1), 21_300, 363_278));
        // 通電時間の無い記録が2件以上（同じ台か確かめようがない）
        WriteLegacy(store, "NOPOH1", "Test NVMe", (d, null, 0), (d.AddDays(1), null, 363_278));
        // v1 の中は辻褄が合うが、今回の値が最後の記録に続かない（1 か月で +20,000 時間）
        WriteLegacy(store, "TAIL1", "Test NVMe", (d, 1_200, 0), (d.AddDays(1), 1_220, 0));

        var o = store.RecordAll(new[]
        {
            With(Nvme("JUMP1", 363_278), powerOnHours: 21_345),
            With(Nvme("NOPOH1", 363_278), powerOnHours: 21_345),
            With(Nvme("TAIL1", 363_278), powerOnHours: 21_345),
        }, T0);
        Assert.All(o, x => { Assert.Equal(BaselineState.LegacyRefused, x.State); Assert.Null(x.Previous); });
        foreach (var s in new[] { "JUMP1", "NOPOH1", "TAIL1" })
            Assert.True(File.Exists(store.LegacyPathFor(s)), s);
    }

    [Fact]
    public void Legacy_file_is_migrated_only_when_it_is_safe()
    {
        var store = new BaselineStore(_dir);
        var d = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(9));
        // v1 も通電時間を記録していた（Counters.From は当時から同じ）。見本の台は 21,345 時間
        void Legacy(string serial, string model, ulong media) =>
            WriteLegacy(store, serial, model, (d.AddDays(-7), 21_200, media), (d, 21_345, media));
        Legacy("MIG1", "Test NVMe", 100);
        Legacy("DUP1", "Test NVMe", 100);
        Legacy("OTHERMODEL", "Some Other Disk", 100);

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

        Assert.Equal(BaselineState.Duplicate, o[1].State);
        Assert.Equal(BaselineState.Duplicate, o[2].State);
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
