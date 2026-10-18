using PartsLife.Checks.Recording;
using static PartsLife.Checks.Tests.RecordFixtures;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 要約の規則（最大・平均・持続した値・制限の印・同時に起きた時間帯・読めなかったことの注記）。
/// 規則は純粋な関数なので、機械が無くても見本の記録で確かめられる。
/// </summary>
public class RecordSummaryTests
{
    private static RunSummary Summarize(SeriesSet s, RunSession? session = null) =>
        Summarizer.Summarize(s, session ?? Session(GpuCaps()));

    // =====================================================================
    // 最大・平均・持続
    // =====================================================================

    [Fact]
    public void Max_avg_and_sustained_clock_use_the_last_third_of_a_busy_run()
    {
        var sum = Summarize(ThermalRamp());

        var temp = sum.Metric("gpu0.temp_c")!;
        Assert.Equal(80, temp.Max, 3);
        Assert.Equal(40.33, temp.Min, 1);          // t=1 の値
        Assert.InRange(temp.Avg, 70, 76);          // 120 秒で上がり、残り 180 秒は 80 ℃

        // 300 秒の記録 → 窓は 100 秒（長さの 1/3）。その間はずっと 80 ℃・2000 MHz で負荷 100%
        Assert.Equal(100, sum.SustainedWindowSec, 3);
        Assert.Equal(2000, sum.Metric("gpu0.clock_mhz")!.Sustained!.Value, 3);
        Assert.Equal(80, temp.Sustained!.Value, 3);
        Assert.Equal(250, sum.Metric("gpu0.power_w")!.Sustained!.Value, 3);
        Assert.Equal(2793, sum.Metric("gpu0.clock_mhz")!.Max, 0);   // 最大は t=1 のとき（2800 - 800/120 = 2793.3）
    }

    [Fact]
    public void Sustained_ignores_an_idle_tail_after_the_workload_ended()
    {
        // 200 秒は負荷 100% でクロック 2000、そのあと 100 秒は待機（負荷 0%・クロック 300）。持続した値は待機を拾わない
        var s = Series(300,
            ("gpu0.util_pct", t => t <= 200 ? 100 : 0),
            ("gpu0.clock_mhz", t => t <= 200 ? 2000 : 300),
            ("gpu0.temp_c", t => t <= 200 ? 75 : 45));
        var sum = Summarize(s);
        Assert.Equal(2000, sum.Metric("gpu0.clock_mhz")!.Sustained!.Value, 3);
        Assert.Equal(75, sum.Metric("gpu0.temp_c")!.Sustained!.Value, 3);
        Assert.Equal(75, sum.Metric("gpu0.temp_c")!.Max, 3);
    }

    [Fact]
    public void Runs_shorter_than_a_minute_give_no_sustained_values_and_say_so()
    {
        var s = Series(45, ("gpu0.util_pct", _ => 100), ("gpu0.clock_mhz", _ => 2500));
        var sum = Summarize(s);
        Assert.Null(sum.Metric("gpu0.clock_mhz")!.Sustained);
        Assert.Equal(0, sum.SustainedWindowSec);
        var note = Assert.Single(sum.Notes, n => n.Code == "too-short");
        Assert.Equal("45", note.Detail);
        Assert.Equal(2500, sum.Metric("gpu0.clock_mhz")!.Max, 3);   // 最大・平均は短くても出す
    }

    [Fact]
    public void A_device_that_never_got_busy_is_flagged_instead_of_judged()
    {
        var s = Series(120, ("cpu.load_pct", _ => 90), ("gpu0.util_pct", t => t % 7 == 0 ? 6 : 1), ("gpu0.temp_c", _ => 35));
        var sum = Summarize(s);
        var note = Assert.Single(sum.Notes, n => n.Code == "device-idle");
        Assert.Equal("gpu0", note.Subject);
        Assert.Null(sum.Metric("gpu0.temp_c")!.Sustained);
        Assert.NotNull(sum.Metric("cpu.load_pct")!.Sustained);
    }

    [Fact]
    public void Missing_values_are_skipped_and_a_sparse_column_is_reported()
    {
        var s = Series(100, ("gpu0.util_pct", _ => 100), ("gpu0.temp_c", t => t % 2 == 0 ? double.NaN : 60));
        var sum = Summarize(s);
        var m = sum.Metric("gpu0.temp_c")!;
        Assert.Equal(50, m.Samples);
        Assert.Equal(60, m.Avg, 3);
        var note = Assert.Single(sum.Notes, n => n.Code == "sparse");
        Assert.Equal("gpu0.temp_c", note.Subject);
        Assert.Equal("50", note.Detail);
    }

    [Fact]
    public void Empty_recording_is_reported_not_crashed()
    {
        var sum = Summarize(new SeriesSet(Array.Empty<double>(), new Dictionary<string, double[]>()));
        Assert.Equal(0, sum.Samples);
        Assert.Contains(sum.Notes, n => n.Code == "no-samples");
    }

    // =====================================================================
    // 制限の印
    // =====================================================================

    [Fact]
    public void Gpu_limit_bits_are_named_and_counted_in_seconds()
    {
        // 0..60 秒: 負荷 100%。0〜30 秒は電力上限、30〜60 秒は電力上限＋温度、60 秒以降は待機
        var s = Series(100,
            ("gpu0.util_pct", t => t <= 60 ? 100 : 0),
            ("gpu0.throttle", t => t <= 30 ? 0x4 : t <= 60 ? 0x4 | 0x20 : 0x1));
        var limits = Assert.Single(Summarize(s).Limits, l => l.Device == "gpu0");

        double Secs(string name) => limits.Reasons.Single(r => r.Name == name).Seconds;
        Assert.Equal(60, Secs("sw-power-cap"));
        Assert.Equal(30, Secs("sw-thermal-slowdown"));
        Assert.Equal(40, Secs("gpu-idle"));
        Assert.DoesNotContain(limits.Reasons, r => r.Name == "hw-thermal-slowdown");
        // 負荷が高かった間の秒数は、全体の秒数を超えない
        Assert.InRange(limits.Reasons.Single(r => r.Name == "sw-power-cap").SecondsWhileBusy, 60, 63);
        Assert.InRange(limits.Reasons.Single(r => r.Name == "gpu-idle").SecondsWhileBusy, 0, 3);   // 5 秒の移動平均のため、待機に入った直後の 2 秒は「高負荷」に残る
    }

    [Fact]
    public void Cpu_limits_come_from_the_os_indicators_and_nothing_is_invented()
    {
        var s = Series(100,
            ("cpu.load_pct", _ => 100),
            ("cpu.perf_limit_pct", t => t is > 20 and <= 50 ? 80 : 100),
            ("cpu.perf_limit_flags", t => t > 90 ? 1 : 0));
        var l = Assert.Single(Summarize(s).Limits, x => x.Device == "cpu");
        Assert.Equal(30, l.Reasons.Single(r => r.Name == "os-performance-limit-below-100").Seconds);
        Assert.Equal(10, l.Reasons.Single(r => r.Name == "os-performance-limit-flags-set").Seconds);

        // 印が一度も立たなければ、理由は空（「制限なし」とは書かず、立たなかったことだけを残す）
        var clean = Series(100, ("cpu.load_pct", _ => 100), ("cpu.perf_limit_pct", _ => 100));
        Assert.Empty(Assert.Single(Summarize(clean).Limits).Reasons);
    }

    // =====================================================================
    // 同時に起きた時間帯（原因は言わない）
    // =====================================================================

    [Fact]
    public void Temperature_rising_while_the_clock_drops_under_load_is_listed_as_a_co_occurrence()
    {
        var sum = Summarize(ThermalRamp());
        var c = Assert.Single(sum.CoOccurrences);
        Assert.Equal("gpu0", c.Device);
        Assert.True(c.TempToC - c.TempFromC >= 3);
        Assert.True(c.ClockFromMHz > c.ClockToMHz);
        Assert.InRange(c.StartSec, 1, 5);
        Assert.InRange(c.EndSec, 120, 160);
        Assert.True(c.AvgLoadPct >= 99);
    }

    [Fact]
    public void A_temperature_rise_without_a_clock_drop_is_not_a_co_occurrence()
    {
        Assert.Empty(Summarize(ThermalRamp(clock: _ => 2800)).CoOccurrences);
    }

    [Fact]
    public void A_clock_drop_without_a_temperature_rise_is_not_a_co_occurrence()
    {
        var s = Series(300, ("gpu0.util_pct", _ => 100), ("gpu0.temp_c", _ => 60), ("gpu0.clock_mhz", t => 2800 - 800 * Math.Min(t, 120) / 120.0));
        Assert.Empty(Summarize(s).CoOccurrences);
    }

    [Fact]
    public void Co_occurrence_needs_real_load_so_idle_cooldowns_are_not_reported()
    {
        // 温度が上がりクロックが下がっているが、負荷は 10%（待機の揺れ）。負荷の無い時間帯は拾わない
        Assert.Empty(Summarize(ThermalRamp(util: _ => 10)).CoOccurrences);
    }

    [Fact]
    public void Co_occurrence_wording_says_it_happened_together_and_never_claims_a_cause()
    {
        var c = Assert.Single(Summarize(ThermalRamp()).CoOccurrences);
        string ja = RecordText.CoOccurrence(c, "ja"), en = RecordText.CoOccurrence(c, "en");
        Assert.Contains("同じ時間帯", ja);
        Assert.Contains("原因は分かりません", ja);
        Assert.Contains("same time window", en);
        Assert.Contains("cause cannot be told", en);
        Assert.DoesNotContain("せいで", ja);
        Assert.DoesNotContain("because", en, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GPU0", ja);
    }

    // =====================================================================
    // 読めなかったこと・記録の傷
    // =====================================================================

    [Fact]
    public void Unreadable_items_become_notes_with_their_state_and_reason()
    {
        var caps = new[]
        {
            Avail("cpu.load_pct"),
            Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver),
            Missing("gpu1.temp_c", Availability.Unavailable, Reasons.NoAdlx),
            Avail("cpu.lp0.load_pct"),
        };
        var sum = Summarize(Series(30, ("cpu.load_pct", _ => 50)), Session(caps));
        var notes = sum.Notes.Where(n => n.Code == "unavailable").ToDictionary(n => n.Subject, n => n.Detail);
        Assert.Equal("needs-extension:needs-driver", notes["cpu.temp_c"]);
        Assert.Equal("unavailable:no-adlx", notes["gpu1.temp_c"]);
        Assert.Equal(2, notes.Count);   // 読めている項目・論理プロセッサごとの項目は注記にしない
    }

    [Fact]
    public void A_cpu_clock_drop_without_a_cpu_temperature_says_the_cause_cannot_be_narrowed_down()
    {
        // CPU は終始 100% で、クロックが 5000 → 4000 に落ちて落ち着いた。CPU の温度は列に無い
        var s = Series(300, ("cpu.load_pct", _ => 100), ("cpu.clock_mhz", t => t <= 60 ? 5000 : 4000));
        var sum = Summarize(s, Session(new[] { Avail("cpu.load_pct"), Avail("cpu.clock_mhz"), Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver) }));
        Assert.Contains(sum.Notes, n => n.Code == "cpu-clock-drop-no-temp");
        Assert.Empty(sum.CoOccurrences);
    }

    [Fact]
    public void Gaps_in_the_recording_and_an_unfinished_run_are_reported()
    {
        // 50 秒目の次が 65 秒目（15 秒の空き）
        var t = Enumerable.Range(1, 100).Select(i => (double)(i <= 50 ? i : i + 14)).ToArray();
        var cols = new Dictionary<string, double[]> { ["cpu.load_pct"] = t.Select(_ => 50.0).ToArray() };
        var sum = Summarizer.Summarize(new SeriesSet(t, cols), Session(status: "interrupted"));
        Assert.Equal(15, sum.MaxGapSec, 3);
        Assert.Equal(14, sum.GapSec, 3);
        Assert.Contains(sum.Notes, n => n.Code == "gaps");
        Assert.Contains(sum.Notes, n => n.Code == "not-completed" && n.Subject == "interrupted");
    }

    [Fact]
    public void Battery_use_is_noted_when_the_power_source_changed()
    {
        var s = Series(60, ("cpu.load_pct", _ => 50), ("power.ac", t => t < 30 ? 1 : 0));
        Assert.Contains(Summarize(s).Notes, n => n.Code == "battery-used");
        var ac = Series(60, ("cpu.load_pct", _ => 50), ("power.ac", _ => 1));
        Assert.DoesNotContain(Summarize(ac).Notes, n => n.Code == "battery-used");
    }

    [Fact]
    public void Per_logical_processor_spread_shows_a_single_thread_load()
    {
        var cols = new Dictionary<string, double[]> { ["cpu.load_pct"] = new double[] { 6, 6, 6 } };
        var lp = new double[] { 100, 3, 2, 2, 3, 2, 1, 2 };
        var sum = Summarizer.Summarize(new SeriesSet(new double[] { 1, 2, 3 }, cols, lp), Session());
        Assert.Equal(8, sum.PerCore!.LogicalProcessors);
        Assert.Equal(100, sum.PerCore.AvgOfMax);
        Assert.Equal(1, sum.PerCore.ActiveCount);
    }

    // =====================================================================
    // 目印の区間
    // =====================================================================

    [Fact]
    public void Markers_cut_out_bench_and_stress_spans_with_their_own_numbers()
    {
        var s = Series(100, ("gpu0.util_pct", t => t is >= 21 and <= 60 ? 100 : 5), ("gpu0.power_w", t => t is >= 21 and <= 60 ? 200 : 30));
        var session = Session(GpuCaps());
        var t0 = session.StartUtc;
        session.Markers.Add(new RunMarker(MarkerKind.BenchStart, 20.5, t0));
        session.Markers.Add(new RunMarker(MarkerKind.BenchEnd, 60.5, t0));
        session.Markers.Add(new RunMarker(MarkerKind.StressStart, 80, t0));   // 終わりの印が無い → 記録の終わりまで
        var sum = Summarizer.Summarize(s, session);

        var bench = sum.Spans.Single(x => x.Kind == "bench");
        Assert.False(bench.Open);
        Assert.Equal(200, bench.Metrics.Single(m => m.Key == "gpu0.power_w").Avg, 3);
        Assert.Equal(100, bench.Metrics.Single(m => m.Key == "gpu0.util_pct").Median, 3);

        var stress = sum.Spans.Single(x => x.Kind == "stress");
        Assert.True(stress.Open);
        Assert.Equal(100, stress.EndSec, 3);
        Assert.Equal(30, stress.Metrics.Single(m => m.Key == "gpu0.power_w").Avg, 3);
    }

    [Fact]
    public void An_end_mark_without_a_start_is_noted_and_does_not_make_a_span()
    {
        var session = Session();
        session.Markers.Add(new RunMarker(MarkerKind.BenchEnd, 10, session.StartUtc));
        var sum = Summarizer.Summarize(Series(30, ("cpu.load_pct", _ => 10)), session);
        Assert.Empty(sum.Spans);
        Assert.Contains(sum.Notes, n => n.Code == "marker-unpaired" && n.Subject == "bench-end");
    }

    // =====================================================================
    // 統計の小道具
    // =====================================================================

    [Fact]
    public void Median_and_trailing_mean_ignore_missing_values()
    {
        Assert.Equal(3, Stat.Median(new List<double> { 5, 1, 3 }));
        Assert.Equal(2.5, Stat.Median(new List<double> { 4, 1, 2, 3 }));
        var tm = Stat.TrailingMean(new[] { 10, double.NaN, 20, 30, 40 }, 3);
        Assert.Equal(10, tm[0]);
        Assert.Equal(10, tm[1]);            // NaN は数えない
        Assert.Equal(15, tm[2]);
        Assert.Equal(25, tm[3]);
        Assert.Equal(30, tm[4]);
    }
}
