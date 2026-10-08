using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Tests;

public class BaselineTests : IDisposable
{
    /// <summary>テストの書き込みはビルド出力の下だけに置く。</summary>
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "baseline-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static DiskReport Nvme(string serial, ulong mediaErrors) => new()
    {
        Number = 0, Model = "Test NVMe", Serial = serial, Bus = BusType.Nvme, Protocol = "nvme",
        NvmeHealthPage = Fixtures.FailingNvme(mediaErrors),
    };

    [Fact]
    public void Delta_is_formatted_like_the_owner_expects()
    {
        var d = new CounterDelta(Counters.MediaErrors, 363_278, 363_301, DateTimeOffset.Now);
        Assert.Equal("メディアエラー 363,278 → 363,301 (+23)", d.Format());
        Assert.True(d.Grew);
        Assert.Equal(23, d.Change);
    }

    [Fact]
    public void Record_returns_the_previous_entry_and_keeps_history_per_serial()
    {
        var store = new BaselineStore(_dir);
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(9));

        Assert.Null(store.Record(Nvme("SER-A", 363_278), t0));
        Assert.Null(store.Record(Nvme("SER-B", 0), t0));          // 別の台は別の履歴

        var prev = store.Record(Nvme("SER-A", 363_301), t0.AddDays(8));
        Assert.NotNull(prev);
        Assert.Equal(363_278UL, prev!.Counters[Counters.MediaErrors]);
        Assert.Equal(t0, prev.At);

        var history = store.Load("SER-A");
        Assert.Equal(2, history.Entries.Count);
        Assert.Equal("Test NVMe", history.Model);
        Assert.Single(store.Load("SER-B").Entries);
    }

    [Fact]
    public void Diff_only_compares_counters_present_in_both()
    {
        var prev = new BaselineEntry { At = DateTimeOffset.Now, Counters = { ["a"] = 1, ["b"] = 5 } };
        var cur = new Dictionary<string, ulong> { ["a"] = 3, ["c"] = 9 };
        var d = BaselineStore.Diff(prev, cur);
        var only = Assert.Single(d);
        Assert.Equal("a", only.Counter);
        Assert.Equal(2, only.Change);
        Assert.Empty(BaselineStore.Diff(null, cur));
    }

    [Fact]
    public void Shrinking_counter_is_reported_as_negative()
    {
        var d = new CounterDelta("x", 10, 7, DateTimeOffset.Now);
        Assert.Equal(-3, d.Change);
        Assert.False(d.Grew);
        Assert.EndsWith("(-3)", d.Format());
    }

    [Theory]
    [InlineData("S7KH NJ0W/123", "S7KH_NJ0W_123")]
    [InlineData("  ..  ", "unknown")]
    [InlineData("WD-WX12A3456789", "WD-WX12A3456789")]
    public void Serials_become_safe_file_names(string serial, string expected)
    {
        Assert.Equal(expected, BaselineStore.SafeName(serial));
    }

    [Fact]
    public void History_is_capped()
    {
        var store = new BaselineStore(_dir);
        var t = DateTimeOffset.Now;
        for (int i = 0; i < BaselineStore.MaxEntries + 5; i++) store.Record(Nvme("CAP", (ulong)i), t.AddMinutes(i));
        Assert.Equal(BaselineStore.MaxEntries, store.Load("CAP").Entries.Count);
    }
}

public class StorageJudgeTests
{
    private static DiskReport NvmeReport(byte[] page) => new()
    {
        Model = "NVMe", Serial = "X", Bus = BusType.Nvme, Protocol = "nvme", NvmeHealthPage = page,
        NvmeErrorPage = new byte[64 * 4],
    };

    private static Finding Get(DiskJudgement j, string title) => j.Findings.Single(f => f.Title == title);

    [Fact]
    public void Healthy_nvme_is_ok_everywhere_it_was_measured()
    {
        var j = StorageJudge.Judge(NvmeReport(Fixtures.HealthyNvme()), null);
        Assert.Equal(Verdict.Ok, j.Overall);
        Assert.Equal("0 件", Get(j, "メディアエラー").Reason);
    }

    [Fact]
    public void Critical_warning_bit_is_a_check()
    {
        var j = StorageJudge.Judge(NvmeReport(Fixtures.FailingNvme()), null);
        Assert.Equal(Verdict.Check, Get(j, "重大警告").Verdict);
        Assert.Contains("0x05", Get(j, "重大警告").Reason);
        Assert.Equal(Verdict.Check, Get(j, "予備領域").Verdict);
        Assert.Equal(Verdict.Check, Get(j, "寿命の消費").Verdict);
        Assert.Equal(Verdict.Check, j.Overall);
    }

    [Fact]
    public void First_check_with_media_errors_asks_to_wait_for_the_next_one()
    {
        var j = StorageJudge.Judge(NvmeReport(Fixtures.FailingNvme(363_278)), null);
        var m = Get(j, "メディアエラー");
        Assert.Equal(Verdict.Caution, m.Verdict);
        Assert.Contains("363,278", m.Reason);
        Assert.Contains("次回", m.Reason);
    }

    [Fact]
    public void Growing_media_errors_are_a_check_and_show_the_delta()
    {
        var prev = new BaselineEntry
        {
            At = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            Counters = { [Counters.MediaErrors] = 363_278 },
        };
        var j = StorageJudge.Judge(NvmeReport(Fixtures.FailingNvme(363_301)), prev);
        var m = Get(j, "メディアエラー");
        Assert.Equal(Verdict.Check, m.Verdict);
        Assert.Contains("メディアエラー 363,278 → 363,301 (+23)", m.Reason);
    }

    [Fact]
    public void Stable_historical_media_errors_are_not_alarming()
    {
        var page = Fixtures.HealthyNvme();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(160), 363_278);
        var prev = new BaselineEntry { At = DateTimeOffset.Now.AddDays(-7), Counters = { [Counters.MediaErrors] = 363_278 } };
        var j = StorageJudge.Judge(NvmeReport(page), prev);
        var m = Get(j, "メディアエラー");
        Assert.Equal(Verdict.Ok, m.Verdict);
        Assert.Contains("増えていません", m.Reason);
    }

    [Fact]
    public void Media_error_in_the_error_log_is_noted()
    {
        var r = NvmeReport(Fixtures.HealthyNvme());
        r.NvmeErrorPage = Fixtures.NvmeErrorLog();
        var j = StorageJudge.Judge(r, null);
        Assert.Equal(Verdict.Caution, Get(j, "エラーログ").Verdict);
    }

    [Fact]
    public void Sata_with_pending_sectors_is_a_check()
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        var r = new DiskReport { Model = "HDD", Serial = "WD-1", Bus = BusType.Sata, Protocol = "ata", AtaSmartData = d, AtaSmartThresholds = t };
        var j = StorageJudge.Judge(r, null);
        var pending = j.Findings.Single(f => f.Title == "代替待ちセクタ");
        Assert.Equal(Verdict.Check, pending.Verdict);
        Assert.StartsWith("8。", pending.Reason);
        Assert.Equal(Verdict.Ok, j.Findings.Single(f => f.Title == "代替処理済みセクタ").Verdict);
        Assert.Equal(Verdict.Caution, j.Findings.Single(f => f.Title == "通信路の CRC エラー").Verdict);
        Assert.Equal(Verdict.Ok, j.Findings.Single(f => f.Title == "閾値").Verdict);
        Assert.Equal("38 °C", j.Findings.Single(f => f.Title == "温度").Reason);
        Assert.Equal(Verdict.Check, j.Overall);
    }

    [Fact]
    public void Pending_sectors_that_did_not_grow_still_need_attention()
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        var r = new DiskReport { Serial = "WD-1", Bus = BusType.Sata, Protocol = "ata", AtaSmartData = d, AtaSmartThresholds = t };
        var prev = new BaselineEntry { At = DateTimeOffset.Now.AddDays(-3), Counters = { ["ata.197"] = 8 } };
        var j = StorageJudge.Judge(r, prev);
        Assert.Equal(Verdict.Check, j.Findings.Single(f => f.Title == "代替待ちセクタ").Verdict);
    }

    [Fact]
    public void Drive_own_threshold_failure_is_reported()
    {
        var (d, t) = Fixtures.SataFailingThreshold();
        var r = new DiskReport { Serial = "S", Bus = BusType.Sata, Protocol = "ata", AtaSmartData = d, AtaSmartThresholds = t };
        var j = StorageJudge.Judge(r, null);
        Assert.Equal(Verdict.Check, j.Findings.Single(f => f.Title == "閾値").Verdict);
    }

    [Fact]
    public void Usb_disk_is_unavailable_not_ok()
    {
        var r = new DiskReport { Serial = "U", Bus = BusType.Usb, Unavailable = "この接続（USB）では健康情報を読めません" };
        var j = StorageJudge.Judge(r, null);
        Assert.Equal(Verdict.Unavailable, j.Overall);
    }

    [Fact]
    public void Counters_from_report_include_the_watched_ones()
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        var c = Counters.From(new DiskReport { AtaSmartData = d, AtaSmartThresholds = t });
        Assert.Equal(8UL, c["ata.197"]);
        Assert.Equal(3UL, c["ata.199"]);
        Assert.Equal(21_000UL, c["ata.9"]);
        Assert.Equal(Verdict.Check, Counters.GrowthVerdict("ata.197"));
        Assert.Null(Counters.GrowthVerdict(Counters.PowerOnHours));
    }
}
