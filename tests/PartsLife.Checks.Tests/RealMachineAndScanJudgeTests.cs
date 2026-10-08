using PartsLife.Checks.Gpu;
using PartsLife.Checks.Scan;
using PartsLife.Checks.SystemInfo;

namespace PartsLife.Checks.Tests;

public class ScanJudgeTests
{
    [Fact]
    public void Clean_sampled_scan_says_what_it_did_not_read()
    {
        var r = new ScanResult { Blocks = 100, ReadBytes = 100L << 20, DiskBytes = 25_600L << 20, Every = 256, MedianMs = 3, MaxMs = 9, Seconds = 4 };
        var f = ScanJudge.Judge(r);
        Assert.Equal(Verdict.Ok, f.Single(x => x.Title == "読めない所").Verdict);
        Assert.Equal(Verdict.Ok, f.Single(x => x.Title == "遅い所").Verdict);
        Assert.Contains("読んでいない所の状態は分かりません", f.Single(x => x.Title == "範囲").Reason);
    }

    [Fact]
    public void Read_errors_are_a_check_with_locations()
    {
        var r = new ScanResult { Blocks = 10, ErrorCount = 2, Full = true, DiskBytes = 1L << 40, ReadBytes = 8L << 20, MedianMs = 1, MaxMs = 2 };
        r.Errors.Add(new ScanIssue { Offset = 123_000_000_000, Length = 1 << 20, Error = 23 });
        var f = ScanJudge.Judge(r).Single(x => x.Title == "読めない所");
        Assert.Equal(Verdict.Check, f.Verdict);
        Assert.Contains("123.0 GB", f.Reason);
    }

    [Fact]
    public void Slow_blocks_are_noted_and_nothing_read_is_unavailable()
    {
        var slow = new ScanResult { Blocks = 1000, SlowCount = 3, ReadBytes = 1, DiskBytes = 1, MedianMs = 8, MaxMs = 900, Full = true };
        Assert.Equal(Verdict.Caution, ScanJudge.Judge(slow).Single(x => x.Title == "遅い所").Verdict);

        var none = new ScanResult { AbortReason = "ディスクを開けませんでした（5）" };
        var f = Assert.Single(ScanJudge.Judge(none));
        Assert.Equal(Verdict.Unavailable, f.Verdict);
    }
}

public class GpuAdapterDedupeTests
{
    private static GpuAdapter A(int i, string name, ulong vram, uint? bus) =>
        new(i, name, 0x10DE, vram, (uint)i, 0, bus, bus is null ? null : 0u, null);

    [Fact]
    public void Alias_without_pci_address_is_dropped()
    {
        // 実機: 仮想ディスプレイのドライバが RTX 5070 をもう1枚見せていた（番地なし）
        var list = new List<GpuAdapter> { A(0, "RTX 5070", 12, 1), A(1, "Radeon", 1, 17), A(2, "RTX 3080 Ti", 12, 9), A(3, "RTX 5070", 12, null) };
        var d = GpuAdapters.Dedupe(list);
        Assert.Equal(new[] { 0, 1, 2 }, d.Select(x => x.DxgiIndex).ToArray());
    }

    [Fact]
    public void Two_identical_real_cards_are_both_kept()
    {
        var list = new List<GpuAdapter> { A(0, "RTX 3080 Ti", 12, 1), A(1, "RTX 3080 Ti", 12, 9) };
        Assert.Equal(2, GpuAdapters.Dedupe(list).Count);
    }
}

public class RealMachineReadOnlyTests
{
    /// <summary>
    /// 数える仕組みが本当に数えているか（陽性対照）。
    /// **「0 件」しか見たことのない数え上げは、壊れていても 0 件と言い続ける。**
    /// 起動の記録（Kernel-General の情報）は 7 日以内に必ずある。
    /// </summary>
    [Fact]
    public void Event_counter_counts_something_that_always_exists()
    {
        var n = EventCounts.Count(new[] { "Microsoft-Windows-Kernel-General" }, new[] { 0, 4 });
        Assert.NotNull(n);
        Assert.True(n > 0, "the event counter returned 0 for boot events");
    }

    [Fact]
    public void Video_code_22_is_a_disabled_device_not_a_fault()
    {
        var f = SystemCheck.JudgeVideo(new VideoInfo("Virtual Desktop Monitor", "13.50", new DateTime(2024, 5, 21), 22), new DateTime(2026, 10, 9));
        Assert.Equal(Verdict.Ok, f.Verdict);
        Assert.Contains("無効", f.Reason);
    }
}
