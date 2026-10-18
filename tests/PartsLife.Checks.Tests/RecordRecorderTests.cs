using System.Text.Json;
using PartsLife.Checks.Gpu;
using PartsLife.Checks.Recording;
using static PartsLife.Checks.Tests.RecordFixtures;

namespace PartsLife.Checks.Tests;

/// <summary>見本の読み口。読めるか・何を返すか・固まるかを試験が決める。</summary>
internal sealed class FakeSource : ISensorSource
{
    private readonly IReadOnlyList<MetricStatus> _caps;
    private readonly Action<SampleRow, int>? _read;
    private readonly Func<IReadOnlyList<MetricStatus>>? _detect;
    public int Reads;
    public bool Disposed;

    public FakeSource(string id, IReadOnlyList<MetricStatus> caps, Action<SampleRow, int>? read = null, Func<IReadOnlyList<MetricStatus>>? detect = null)
    {
        Id = id; _caps = caps; _read = read; _detect = detect;
    }

    public string Id { get; }
    public IReadOnlyList<MetricStatus> Detect() => _detect is null ? _caps : _detect();
    public void Prime() { }
    public void Read(SampleRow row) => _read?.Invoke(row, ++Reads);
    public void Dispose() => Disposed = true;
}

/// <summary>
/// 試験が 1 秒ずつ進める時計。<see cref="Release"/> を 1 回呼ぶごとに、記録の糸が次の枠まで進む。
/// <see cref="JumpNext"/> で、次の枠を飛び越えた（遅れた）ことにもできる。
/// </summary>
internal sealed class SteppedClock : IRunClock
{
    private readonly SemaphoreSlim _go = new(0);
    private readonly object _l = new();
    private double _mono;
    private double? _jump;
    public double MonotonicSeconds { get { lock (_l) return _mono; } }
    public DateTime UtcNow => new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc).AddSeconds(MonotonicSeconds);
    public void Release(int n = 1) => _go.Release(n);
    public void JumpNext(double toSeconds) { lock (_l) _jump = toSeconds; }

    public void WaitUntil(double target, CancellationToken cancel)
    {
        try { _go.Wait(cancel); }
        catch (OperationCanceledException) { return; }
        lock (_l) { _mono = Math.Max(_mono, _jump ?? target); _jump = null; }
    }
}

/// <summary>
/// 記録の通し（開始・毎秒・目印・停止・ファイル）と、読み口の失敗への備え（検出の失敗・固まり・例外・重複）。
/// 時計と読み口は差し替え、待たずに回す。
/// </summary>
public class RecordRecorderTests : IDisposable
{
    private readonly string _dir = TempFolder();
    private readonly List<ManualResetEventSlim> _gates = new();
    public void Dispose()
    {
        foreach (var g in _gates) g.Set();     // 固まらせた糸を放す
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ManualResetEventSlim Gate() { var g = new ManualResetEventSlim(false); _gates.Add(g); return g; }

    private RunOptions Options(SteppedClock clock, Func<SourceSet> sources, TimeSpan? max = null, long minFree = 0) => new()
    {
        Folder = _dir,
        AppVersion = "0.7.4",
        Clock = clock,
        Sources = sources,
        MaxDuration = max ?? TimeSpan.FromHours(12),
        MinFreeBytes = minFree,
        NewRunId = () => "abcd2345efgh6789",
        System = _ => new SystemSnapshot("Windows 11 Pro", 26300, "balanced", 65536, 16),
        DetectTimeout = TimeSpan.FromMilliseconds(400),
        ReadTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static SourceSet Fake(params ISensorSource[] sources) => new(sources);

    private static FakeSource CpuLoad(Action<SampleRow, int>? read = null) =>
        new("cpu", new[] { Avail("cpu.load_pct") }, read ?? ((row, n) => row.Set("cpu.load_pct", n * 10)));

    /// <summary>n 回ぶん時計を進め、1 回ごとに記録の糸が 1 行書き終わるのを待つ。</summary>
    private static void Tick(RunRecorder rec, SteppedClock clock, int n)
    {
        using var got = new SemaphoreSlim(0);
        void On(LiveSample _) => got.Release();
        rec.Sampled += On;
        try
        {
            for (int i = 0; i < n; i++)
            {
                clock.Release();
                Assert.True(got.Wait(TimeSpan.FromSeconds(10)), "the recording thread did not write a row");
            }
        }
        finally { rec.Sampled -= On; }
    }

    // =====================================================================
    // 通し
    // =====================================================================

    [Fact]
    public async Task One_row_per_tick_with_monotonic_and_utc_stamps_then_a_completed_run_file()
    {
        var clock = new SteppedClock();
        var src = CpuLoad();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(src)));
        Assert.Contains(rec.Capabilities.Metrics, m => m.Key == "cpu.load_pct" && m.IsAvailable);

        rec.Start(new BuildSnapshot("PCB1.abc", BuildJson));
        string json = RunStore.JsonPath(_dir, "abcd2345efgh6789"), csv = RunStore.CsvPath(_dir, "abcd2345efgh6789");
        Assert.True(File.Exists(json) && File.Exists(csv));
        // 始めた直後（まだ止めていない）のファイルは「記録中」。途中で落ちても形が読める
        using (var mid = JsonDocument.Parse(File.ReadAllText(json)))
        {
            Assert.Equal("recording", mid.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, mid.RootElement.GetProperty("endUtc").ValueKind);
        }

        Tick(rec, clock, 5);
        var result = await rec.StopAsync();

        var lines = File.ReadAllLines(csv);
        Assert.Equal("t_s,utc,cpu.load_pct", lines[0]);
        Assert.Equal(6, lines.Length);
        for (int i = 1; i <= 5; i++)
            Assert.Equal($"{i}.000,2026-10-18T12:00:0{i}.000Z,{i * 10}", lines[i]);

        Assert.Equal("completed", result.Session.Status);
        Assert.Equal("user", result.Session.StopReason);
        Assert.Equal(5, result.Session.Stats.Samples);
        Assert.NotNull(result.Summary);
        Assert.Equal(50, result.Summary!.Metric("cpu.load_pct")!.Max);
        using var done = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal("completed", done.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, done.RootElement.GetProperty("endUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, done.RootElement.GetProperty("summary").ValueKind);
        Assert.Equal("PCB1.abc", done.RootElement.GetProperty("build").GetProperty("code").GetString());
        Assert.True(src.Disposed);
        Assert.Equal(new[] { "abcd2345efgh6789" }, RunStore.List(_dir).Select(x => x.RunId).ToArray());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Markers_use_a_fixed_vocabulary_and_are_written_to_the_file_at_once()
    {
        var clock = new SteppedClock();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(CpuLoad())));
        rec.Start(null);
        Tick(rec, clock, 2);
        var m = rec.AddMarker(MarkerKind.BenchStart);
        Assert.Equal(2.0, m.TSec);

        // ファイルにすぐ入っている（止めるのを待たない）
        var json = RunStore.JsonPath(_dir, "abcd2345efgh6789");
        using (var doc = JsonDocument.Parse(File.ReadAllText(json)))
        {
            var marker = Assert.Single(doc.RootElement.GetProperty("markers").EnumerateArray());
            Assert.Equal("bench-start", marker.GetProperty("kind").GetString());
            Assert.Equal(2.0, marker.GetProperty("tSec").GetDouble());
        }

        Tick(rec, clock, 3);
        rec.AddMarker(MarkerKind.BenchEnd);
        rec.AddMarker(MarkerKind.Note);
        var result = await rec.StopAsync();
        Assert.Equal(new[] { "bench-start", "bench-end", "mark" }, result.Session.Markers.Select(x => MarkerKinds.Code(x.Kind)).ToArray());
        // 目印の種類は 5 つだけ（自由な文字は受け付けない）
        Assert.Equal(5, Enum.GetValues<MarkerKind>().Length);
        var span = Assert.Single(result.Summary!.Spans);
        Assert.Equal("bench", span.Kind);
        Assert.Equal(2.0, span.StartSec);
    }

    [Fact]
    public async Task A_late_tick_is_skipped_not_replayed_and_shows_up_as_a_gap()
    {
        var clock = new SteppedClock();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(CpuLoad())));
        rec.Start(null);
        Tick(rec, clock, 2);          // t = 1, 2
        clock.JumpNext(7.3);          // 次の枠で目が覚めたのが 7.3 秒（5 秒ぶん遅れた）
        Tick(rec, clock, 1);
        Tick(rec, clock, 2);          // 枠 8, 9
        var result = await rec.StopAsync();

        var t = CsvStream.Read(result.CsvPath).T;
        Assert.Equal(new[] { 1.0, 2.0, 7.3, 8.0, 9.0 }, t);    // 取りこぼした 3〜6 を詰め込んだりしない
        Assert.Equal(5.3, result.Session.Stats.MaxGapSec, 3);
        Assert.Equal(4.3, result.Session.Stats.GapSec, 3);
        Assert.Contains(result.Summary!.Notes, n => n.Code == "gaps");
    }

    [Fact]
    public async Task It_stops_by_itself_at_the_maximum_duration()
    {
        var clock = new SteppedClock();
        var ended = new TaskCompletionSource<string>();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(CpuLoad()), max: TimeSpan.FromSeconds(3)));
        rec.Ended += r => ended.TrySetResult(r);
        rec.Start(null);
        clock.Release(10);                               // 3 回目で上限に達して止まる
        Assert.Equal("max-duration", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var result = await rec.Completion;
        Assert.Equal(3, result.Session.Stats.Samples);
        Assert.Equal("completed", result.Session.Status);
        Assert.Equal("max-duration", result.Session.StopReason);
        Assert.False(rec.IsRecording);
    }

    [Fact]
    public async Task Stopping_twice_is_harmless_and_markers_need_a_recording()
    {
        var clock = new SteppedClock();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(CpuLoad())));
        Assert.Equal("state", Assert.Throws<RecordException>(() => rec.AddMarker(MarkerKind.Note)).Code);
        Assert.Equal("state", Assert.Throws<RecordException>(() => { rec.StopAsync().GetAwaiter().GetResult(); }).Code);
        rec.Start(null);
        Tick(rec, clock, 1);
        var a = await rec.StopAsync();
        var b = await rec.StopAsync();
        Assert.Same(a, b);
        Assert.Equal("state", Assert.Throws<RecordException>(() => rec.AddMarker(MarkerKind.Note)).Code);
        Assert.Equal("state", Assert.Throws<RecordException>(() => rec.Start(null)).Code);
    }

    // =====================================================================
    // 始められない
    // =====================================================================

    [Fact]
    public void It_refuses_to_start_when_the_disk_is_nearly_full_and_creates_no_files()
    {
        var clock = new SteppedClock();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(CpuLoad()), minFree: long.MaxValue));
        Assert.Equal("low-disk", Assert.Throws<RecordException>(() => rec.Start(null)).Code);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.False(rec.IsRecording);
    }

    [Fact]
    public void An_unwritable_folder_is_a_clear_error_not_a_crash()
    {
        string file = Path.Combine(_dir, "iam-a-file");
        File.WriteAllText(file, "x");
        var o = new RunOptions
        {
            Folder = Path.Combine(file, "runs"),     // ファイルの下にフォルダは作れない
            Clock = new SteppedClock(), Sources = () => Fake(CpuLoad()), MinFreeBytes = 0,
            System = _ => new SystemSnapshot("", 0, "", 0, 0),
        };
        using var rec = RunRecorder.Prepare(o);
        Assert.Equal("folder", Assert.Throws<RecordException>(() => rec.Start(null)).Code);
    }

    [Fact]
    public void Disposing_a_prepared_recorder_closes_the_sources_and_writes_nothing()
    {
        var src = CpuLoad();
        var rec = RunRecorder.Prepare(Options(new SteppedClock(), () => Fake(src)));
        rec.Dispose();
        Assert.True(src.Disposed);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Probe_reports_what_is_readable_and_leaves_nothing_behind()
    {
        var src = new FakeSource("x", new[] { Avail("cpu.load_pct"), Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver) });
        var report = RunRecorder.Probe(Options(new SteppedClock(), () => Fake(src)));
        Assert.Equal(1, report.Count(Availability.Available));
        Assert.Equal(1, report.Count(Availability.NeedsExtension));
        Assert.True(src.Disposed);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // =====================================================================
    // 読み口の失敗への備え（検出）
    // =====================================================================

    [Fact]
    public void A_source_that_throws_in_detect_becomes_one_unavailable_line_and_the_others_still_work()
    {
        var bad = new FakeSource("bad", Array.Empty<MetricStatus>(), detect: () => throw new InvalidOperationException("boom"));
        var good = CpuLoad();
        using var hub = SensorHub.Open(Fake(bad, good));
        var line = Assert.Single(hub.Report.Metrics, m => m.Key == "source.bad");
        Assert.Equal(Availability.Unavailable, line.State);
        Assert.Equal(Reasons.Failed, line.Reason);
        Assert.Equal(new[] { "cpu.load_pct" }, hub.Columns);

        var row = new SampleRow(hub.Columns);
        Assert.Equal(0, hub.ReadInto(row));
        Assert.Equal(10, row.Get("cpu.load_pct"));
    }

    [Fact]
    public void A_source_that_hangs_in_detect_times_out_and_is_reported_without_blocking_the_rest()
    {
        var gate = Gate();
        var hang = new FakeSource("hang", Array.Empty<MetricStatus>(), detect: () => { gate.Wait(); return Array.Empty<MetricStatus>(); });
        using var hub = SensorHub.Open(Fake(hang, CpuLoad()), detectTimeout: TimeSpan.FromMilliseconds(200));
        var line = Assert.Single(hub.Report.Metrics, m => m.Key == "source.hang");
        Assert.Equal(Reasons.Timeout, line.Reason);
        Assert.Contains("cpu.load_pct", hub.Columns);
    }

    [Fact]
    public void Unavailable_and_needs_extension_items_never_get_a_column()
    {
        var src = new FakeSource("mix", new[]
        {
            Avail("cpu.load_pct"),
            Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver),
            Missing("gpu0.fan_pct", Availability.Unavailable, Reasons.NotSupported),
        });
        using var hub = SensorHub.Open(Fake(src));
        Assert.Equal(new[] { "cpu.load_pct" }, hub.Columns);
        Assert.Equal(3, hub.Report.Metrics.Count);          // 読めない物も一覧には残る
    }

    [Fact]
    public void Two_sources_claiming_the_same_item_keep_the_first_one_only()
    {
        var first = new FakeSource("a", new[] { Avail("cpu.load_pct") }, (r, n) => r.Set("cpu.load_pct", 1));
        var second = new FakeSource("b", new[] { Avail("cpu.load_pct"), Avail("mem.used_pct") }, (r, n) => { r.Set("cpu.load_pct", 99); r.Set("mem.used_pct", 50); });
        using var hub = SensorHub.Open(Fake(first, second));
        Assert.Equal(new[] { "cpu.load_pct", "mem.used_pct" }, hub.Columns);
        var row = new SampleRow(hub.Columns);
        hub.ReadInto(row);
        Assert.Equal(1, row.Get("cpu.load_pct"));
        Assert.Equal(50, row.Get("mem.used_pct"));
    }

    // =====================================================================
    // 読み口の失敗への備え（毎秒）
    // =====================================================================

    [Fact]
    public void A_source_that_throws_while_reading_leaves_empty_cells_and_the_recording_goes_on()
    {
        var flaky = new FakeSource("flaky", new[] { Avail("gpu0.temp_c") }, (r, n) => { if (n == 2) throw new InvalidOperationException("driver"); r.Set("gpu0.temp_c", 60 + n); });
        using var hub = SensorHub.Open(Fake(flaky, CpuLoad()));
        var row = new SampleRow(hub.Columns);
        Assert.Equal(0, hub.ReadInto(row));
        Assert.Equal(61, row.Get("gpu0.temp_c"));
        Assert.Equal(1, hub.ReadInto(row));                 // 2 回目は失敗として数える
        Assert.True(double.IsNaN(row.Get("gpu0.temp_c")));
        Assert.Equal(20, row.Get("cpu.load_pct"));          // 他の読み口は影響を受けない
        Assert.Equal(0, hub.ReadInto(row));
        Assert.Equal(63, row.Get("gpu0.temp_c"));
    }

    [Fact]
    public async Task A_source_that_hangs_while_reading_is_given_up_on_each_second_without_stopping_the_recording()
    {
        var gate = Gate();
        var hangs = new FakeSource("hang", new[] { Avail("gpu0.temp_c") }, (r, n) => { if (n >= 2) gate.Wait(); r.Set("gpu0.temp_c", 50 + n); });
        var clock = new SteppedClock();
        using var rec = RunRecorder.Prepare(Options(clock, () => Fake(hangs, CpuLoad())));
        rec.Start(null);
        Tick(rec, clock, 4);          // 1 秒目は読めて、2 秒目から固まる
        gate.Set();
        var result = await rec.StopAsync();

        var s = CsvStream.Read(result.CsvPath);
        Assert.Equal(4, s.Count);                               // 記録の糸は止まらず、4 行とも書けた
        var temp = s.Get("gpu0.temp_c")!;
        Assert.Equal(51, temp[0]);
        Assert.True(double.IsNaN(temp[1]) && double.IsNaN(temp[2]) && double.IsNaN(temp[3]));
        Assert.Equal(new[] { 10.0, 20.0, 30.0, 40.0 }, s.Get("cpu.load_pct"));    // CPU の列は影響を受けない
        Assert.True(result.Session.Stats.SourceTimeouts["hang"] >= 1);
        Assert.Contains(result.Summary!.Notes, n => n.Code == "source-timeouts" && n.Subject == "hang");
    }

    // =====================================================================
    // 部品の判定
    // =====================================================================

    [Fact]
    public void Logical_processors_are_ordered_by_group_then_number_and_totals_are_not_processors()
    {
        var names = new[] { "_Total", "0,_Total", "0,10", "0,2", "1,0", "0,1", "junk", "0,x" }.Select(n => (n, 0.0));
        Assert.Equal(new[] { "0,1", "0,2", "0,10", "1,0" }, CpuPdhSource.LogicalProcessors(names));
    }

    [Fact]
    public void Package_power_sums_only_package_meters_and_converts_milliwatts_to_watts()
    {
        var w = CpuPdhSource.PackageWatts(new[]
        {
            ("RAPL_Package0_PKG", 75_000.0), ("RAPL_Package0_Core0_CORE", 2_500.0), ("RAPL_Package1_PKG", 25_000.0), ("_Total", 0.0),
        });
        Assert.Equal(100.0, w);
        Assert.Null(CpuPdhSource.PackageWatts(new[] { ("RAPL_Package0_Core0_CORE", 2_500.0) }));
        Assert.Null(CpuPdhSource.PackageWatts(Array.Empty<(string, double)>()));
    }

    [Fact]
    public void Package_power_outside_a_plausible_range_is_not_used_so_a_wrong_unit_cannot_pass()
    {
        Assert.False(CpuPdhSource.PlausibleWatts(0.4));
        Assert.True(CpuPdhSource.PlausibleWatts(0.5));
        Assert.True(CpuPdhSource.PlausibleWatts(145));
        Assert.True(CpuPdhSource.PlausibleWatts(1500));
        Assert.False(CpuPdhSource.PlausibleWatts(1500.1));
        Assert.False(CpuPdhSource.PlausibleWatts(72_865));      // ミリワットのまま W として扱った場合の値
    }

    [Fact]
    public void Nvml_failure_codes_map_to_clear_reasons()
    {
        Assert.Equal(Reasons.LibFunctionMissing, NvmlGpuSource.ReasonOf(-1));
        Assert.Equal(Reasons.NotSupported, NvmlGpuSource.ReasonOf(3));
        Assert.Equal(Reasons.NoPermission, NvmlGpuSource.ReasonOf(4));
        Assert.Equal(Reasons.Failed, NvmlGpuSource.ReasonOf(15));
        Assert.Equal(Reasons.Failed, NvmlGpuSource.ReasonOf(999));
    }

    [Fact]
    public void Items_needing_a_driver_are_listed_as_needs_extension_and_never_as_readable()
    {
        var statuses = new ExtensionSource().Detect();
        Assert.Equal(new[] { "cpu.temp_c", "cpu.vcore_v", "board.fan_rpm", "board.temp_c" }, statuses.Select(s => s.Key).ToArray());
        Assert.All(statuses, s =>
        {
            Assert.Equal(Availability.NeedsExtension, s.State);
            Assert.Equal(Reasons.NeedsDriver, s.Reason);
        });
        var row = new SampleRow(Array.Empty<string>());
        new ExtensionSource().Read(row);          // 何も書かない
        Assert.Equal(0, row.Count);
    }

    [Fact]
    public void Vendor_gaps_say_why_and_never_invent_values()
    {
        var adapter = new GpuAdapter(0, "Intel Arc", 0x8086, 8UL << 30, 1, 0, 3, 0, null);
        var gap = new GpuGapSource(new[] { (new GpuSlot(2, adapter), Reasons.VendorNotImplemented) });
        var statuses = gap.Detect();
        Assert.Equal(new[] { "gpu2.temp_c", "gpu2.clock_mhz", "gpu2.power_w", "gpu2.fan_pct" }, statuses.Select(s => s.Key).ToArray());
        Assert.All(statuses, s =>
        {
            Assert.Equal(Availability.Unavailable, s.State);
            Assert.Equal(Reasons.VendorNotImplemented, s.Reason);
        });
        Assert.Equal("intel", new GpuSlot(2, adapter).Vendor);
        Assert.Equal("amd", new GpuSlot(0, adapter with { VendorId = 0x1002 }).Vendor);
        Assert.Equal("nvidia", new GpuSlot(0, adapter with { VendorId = 0x10DE }).Vendor);
    }

    [Fact]
    public void Gpu_numbers_follow_the_pci_bus_order_not_the_name_or_the_nvml_order()
    {
        // DXGI の並びは 3080 Ti（バス 9）→ 5070（バス 1）→ 内蔵（バス 17）、NVML の番号は逆さ。記録の番号はバス順
        var a = new[]
        {
            new GpuAdapter(0, "RTX 3080 Ti", 0x10DE, 12UL << 30, 10, 0, 9, 0, 0),
            new GpuAdapter(1, "RTX 5070", 0x10DE, 12UL << 30, 11, 0, 1, 0, 1),
            new GpuAdapter(2, "Radeon", 0x1002, 1UL << 29, 12, 0, 17, 0, null),
            new GpuAdapter(3, "No address", 0x8086, 0, 13, 0, null, null, null),
        };
        var slots = GpuSlots.Number(a);
        Assert.Equal(new[] { "RTX 5070", "RTX 3080 Ti", "Radeon", "No address" }, slots.Select(s => s.Adapter.Name).ToArray());
        Assert.Equal("gpu0", slots[0].Describe().Id);
        Assert.Equal(12288, slots[0].Describe().VramMb);
        // PDH のインスタンス名の LUID と突き合わせる鍵は、盤の側（GpuCounterName）と同じ形
        Assert.Equal(PartsLife.Hardware.GpuCounterName.LuidKey("pid_1_luid_0x00000000_0x0000000B_phys_0_eng_0_engtype_3D"), slots[0].LuidKey);
    }

    [Fact]
    public void The_capability_table_collapses_per_processor_items_into_two_lines()
    {
        var statuses = new List<MetricStatus> { Avail("cpu.load_pct"), Avail("cpu.clock_mhz") };
        for (int i = 0; i < 16; i++) { statuses.Add(Avail($"cpu.lp{i}.load_pct")); statuses.Add(Avail($"cpu.lp{i}.clock_mhz")); }
        statuses.Add(Avail("mem.used_pct"));
        var report = new CapabilityReport(statuses, Array.Empty<GpuDescriptor>(), 16);
        Assert.Equal(new[] { "cpu.load_pct", "cpu.clock_mhz", "cpu.lp*.load_pct", "cpu.lp*.clock_mhz", "mem.used_pct" },
                     report.Display().Select(m => m.Key).ToArray());
    }

    [Fact]
    public void Metric_key_helpers_parse_gpu_keys_and_units()
    {
        Assert.True(MetricKeys.TryParseGpu("gpu12.temp_c", out int i, out string m));
        Assert.Equal((12, "temp_c"), (i, m));
        Assert.False(MetricKeys.TryParseGpu("gpu.temp_c", out _, out _));
        Assert.False(MetricKeys.TryParseGpu("gpux.temp_c", out _, out _));
        Assert.False(MetricKeys.TryParseGpu("cpu.load_pct", out _, out _));
        Assert.True(MetricKeys.IsPerLogicalProcessor("cpu.lp31.load_pct"));
        Assert.False(MetricKeys.IsPerLogicalProcessor("cpu.load_pct"));
        Assert.Equal("°C", MetricKeys.UnitOf("gpu0.temp_c"));
        Assert.Equal("MHz", MetricKeys.UnitOf("cpu.lp3.clock_mhz"));
        Assert.Equal("W", MetricKeys.UnitOf("cpu.pkg_power_w"));
        Assert.Equal("MB/s", MetricKeys.UnitOf("disk.read_mbps"));
    }

    [Fact]
    public void The_system_clock_is_monotonic_and_a_wait_ends_early_when_cancelled()
    {
        var c = SystemRunClock.Instance;
        double a = c.MonotonicSeconds;
        Thread.Sleep(20);
        Assert.True(c.MonotonicSeconds > a);
        using var cts = new CancellationTokenSource(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.WaitUntil(c.MonotonicSeconds + 30, cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(Math.Abs((c.UtcNow - DateTime.UtcNow).TotalSeconds) < 5);
    }

    // =====================================================================
    // 実機（この PC で、管理者なしで読める範囲）
    // =====================================================================

    [Fact]
    public async Task Real_machine_smoke_the_default_sources_record_a_few_seconds_without_admin_or_drivers()
    {
        var o = new RunOptions { Folder = _dir, AppVersion = "test", MinFreeBytes = 0 };
        using var rec = RunRecorder.Prepare(o);

        // CPU の使用率と電源状態はどの Windows でも読める。温度・電圧は読まず、「追加機能が必要」と答える
        var cpu = Assert.Single(rec.Capabilities.Metrics, m => m.Key == "cpu.load_pct");
        Assert.Equal(Availability.Available, cpu.State);
        Assert.Equal(Availability.NeedsExtension, rec.Capabilities.Metrics.Single(m => m.Key == "cpu.temp_c").State);
        Assert.Equal(Availability.NeedsExtension, rec.Capabilities.Metrics.Single(m => m.Key == "cpu.vcore_v").State);
        Assert.DoesNotContain("cpu.temp_c", rec.Capabilities.Metrics.Where(m => m.IsAvailable).Select(m => m.Key));
        Assert.Contains(rec.Capabilities.Metrics, m => m.Key == "mem.used_pct" && m.IsAvailable);

        rec.Start(null);
        var done = await Task.WhenAny(rec.Completion, Task.Delay(TimeSpan.FromSeconds(3.6)));
        Assert.NotSame(rec.Completion, done);          // 3 秒ちょっとでは終わらない（12 時間の上限）
        var result = await rec.StopAsync();

        Assert.True(result.Session.Stats.Samples >= 2, $"samples={result.Session.Stats.Samples}");
        var series = CsvStream.Read(result.CsvPath);
        Assert.True(series.Count >= 2);
        Assert.InRange(series.T[0], 0.9, 1.6);         // 1 秒ごと
        Assert.InRange(series.T[1] - series.T[0], 0.8, 1.3);
        Assert.Contains(series.Get("cpu.load_pct")!, v => !double.IsNaN(v));
        Assert.True(series.Get("mem.used_pct")!.All(v => v is > 0 and <= 100));
        Assert.Equal("completed", result.Session.Status);
        Assert.False(string.IsNullOrEmpty(result.Session.System.OsName));
    }
}
