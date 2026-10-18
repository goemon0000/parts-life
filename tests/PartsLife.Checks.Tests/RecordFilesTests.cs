using System.Globalization;
using System.Text.Json;
using PartsLife.Checks.Recording;
using static PartsLife.Checks.Tests.RecordFixtures;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 記録ファイルの形（run-&lt;ID&gt;.json と sensors-&lt;ID&gt;.csv）。**この形はブラウザ側が後で読む約束**なので、
/// 項目の名前と並びを試験で固定する（変えるときは formatVersion を上げる）。
/// </summary>
public class RecordFilesTests : IDisposable
{
    private readonly string _dir = TempFolder();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static RunSession FullSession()
    {
        var s = Session(new[]
        {
            Avail("cpu.load_pct", "pdh"),
            Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver),
            Missing("gpu1.temp_c", Availability.Unavailable, Reasons.NoAdlx),
        });
        s.Build = new BuildSnapshot("PCB1.abc", BuildJson);
        s.Gpus.Add(new GpuDescriptor(0, "NVIDIA GeForce RTX 5070", "nvidia", 11943));
        s.Columns.AddRange(new[] { "t_s", "utc", "cpu.load_pct" });
        s.Markers.Add(new RunMarker(MarkerKind.BenchStart, 12.5, s.StartUtc.AddSeconds(12.5)));
        s.Stats = new RunStats { Samples = 300, GapSec = 2, MaxGapSec = 3, SourceTimeouts = { ["nvml"] = 4 } };
        return s;
    }

    // =====================================================================
    // run-<ID>.json
    // =====================================================================

    [Fact]
    public void Session_json_has_exactly_the_documented_top_level_fields()
    {
        using var doc = JsonDocument.Parse(SessionJson.Write(FullSession()));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "format", "formatVersion", "app", "runId", "status", "stopReason", "startUtc", "endUtc", "intervalMs",
            "system", "build", "gpus", "capabilities", "stream", "markers", "stats", "summary",
        }, names);

        var r = doc.RootElement;
        Assert.Equal("partslife-run", r.GetProperty("format").GetString());
        Assert.Equal(1, r.GetProperty("formatVersion").GetInt32());
        Assert.Equal("abcd2345efgh6789", r.GetProperty("runId").GetString());
        Assert.Equal("2026-10-18T12:00:00.000Z", r.GetProperty("startUtc").GetString());
        Assert.Equal("PCB1.abc", r.GetProperty("build").GetProperty("code").GetString());
        Assert.Equal("Test CPU 9000X 16-Core Processor",
            r.GetProperty("build").GetProperty("config").GetProperty("p").GetProperty("cpu")[0].GetProperty("n").GetString());
        var stream = r.GetProperty("stream");
        Assert.Equal("sensors-abcd2345efgh6789.csv", stream.GetProperty("file").GetString());
        Assert.Equal("t_s", stream.GetProperty("monotonicColumn").GetString());
        Assert.Equal("utc", stream.GetProperty("utcColumn").GetString());
        Assert.Equal(new[] { "t_s", "utc", "cpu.load_pct" }, stream.GetProperty("columns").EnumerateArray().Select(x => x.GetString()).ToArray());
        var marker = r.GetProperty("markers")[0];
        Assert.Equal("bench-start", marker.GetProperty("kind").GetString());
        Assert.Equal(12.5, marker.GetProperty("tSec").GetDouble());
        var caps = r.GetProperty("capabilities").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c);
        Assert.Equal("available", caps["cpu.load_pct"].GetProperty("state").GetString());
        Assert.Equal("needs-extension", caps["cpu.temp_c"].GetProperty("state").GetString());
        Assert.Equal("needs-driver", caps["cpu.temp_c"].GetProperty("reason").GetString());
        Assert.Equal("unavailable", caps["gpu1.temp_c"].GetProperty("state").GetString());
    }

    [Fact]
    public void Session_json_round_trips_and_never_holds_machine_or_user_names_or_paths()
    {
        var original = FullSession();
        string json = SessionJson.Write(original);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", json);
        Assert.DoesNotContain("\\\\", json);

        var back = SessionJson.Read(json);
        Assert.Equal(original.RunId, back.RunId);
        Assert.Equal(original.StartUtc, back.StartUtc);
        Assert.Equal(original.EndUtc, back.EndUtc);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.System, back.System);
        Assert.Equal(original.Build!.Code, back.Build!.Code);
        Assert.Equal(original.Gpus, back.Gpus);
        Assert.Equal(original.Capabilities, back.Capabilities);
        Assert.Equal(original.Columns, back.Columns);
        Assert.Equal(original.Markers, back.Markers);
        Assert.Equal(300, back.Stats.Samples);
        Assert.Equal(4, back.Stats.SourceTimeouts["nvml"]);
    }

    [Fact]
    public void Reading_refuses_other_formats_future_versions_and_unsafe_run_ids()
    {
        string ok = SessionJson.Write(FullSession());
        Assert.Throws<FormatException>(() => SessionJson.Read("{}"));
        Assert.Throws<FormatException>(() => SessionJson.Read("not json"));
        Assert.Throws<FormatException>(() => SessionJson.Read(ok.Replace("\"formatVersion\": 1", "\"formatVersion\": 2")));
        Assert.Throws<FormatException>(() => SessionJson.Read(ok.Replace("partslife-run", "other-format")));
        // ファイルから読んだ ID でファイル名を作るので、パスを抜ける形は受け付けない
        Assert.Throws<FormatException>(() => SessionJson.Read(ok.Replace("abcd2345efgh6789", "..\\\\..\\\\evil")));
        Assert.Throws<FormatException>(() => SessionJson.Read(ok.Replace("abcd2345efgh6789", "ABCD2345EFGH6789")));
    }

    [Fact]
    public void Run_ids_are_random_lowercase_and_safe_for_file_names()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => RunId.New()).ToList();
        Assert.Equal(2000, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(16, id.Length);
            Assert.True(RunId.IsValid(id));
            Assert.Matches("^[a-z0-9]+$", id);
        });
        foreach (var bad in new[] { "", "short", "../x/../abcdefgh", "abcd2345efgh678/", "ABCD2345EFGH6789", "abcd 2345efgh6789", new string('a', 41) })
            Assert.False(RunId.IsValid(bad), bad);
    }

    [Fact]
    public void Atomic_write_replaces_the_file_and_leaves_no_temporary_file()
    {
        string path = Path.Combine(_dir, "run-x.json");
        RunStore.WriteAtomic(path, "one");
        RunStore.WriteAtomic(path, "two");
        Assert.Equal("two\n", File.ReadAllText(path));
        Assert.Equal(new[] { "run-x.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
        // BOM は付けない（受け手の JSON.parse は BOM を嫌う）
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
    }

    // =====================================================================
    // sensors-<ID>.csv
    // =====================================================================

    [Fact]
    public void Csv_has_a_header_monotonic_and_utc_columns_and_empty_cells_for_unread_values()
    {
        string path = Path.Combine(_dir, "s.csv");
        var cols = new[] { "cpu.load_pct", "gpu0.temp_c" };
        var row = new SampleRow(cols);
        using (var w = new CsvStream.Writer(path, cols))
        {
            row.Set("cpu.load_pct", 12.3456);
            w.WriteRow(1.0004, new DateTime(2026, 10, 18, 12, 0, 1, 4, DateTimeKind.Utc), row);   // gpu0.temp_c は読めなかった（NaN のまま）
            row.Set("gpu0.temp_c", 61);
            w.WriteRow(2.0, new DateTime(2026, 10, 18, 12, 0, 2, DateTimeKind.Utc), row);
        }
        var lines = File.ReadAllLines(path);
        Assert.Equal("t_s,utc,cpu.load_pct,gpu0.temp_c", lines[0]);
        Assert.Equal("1.000,2026-10-18T12:00:01.004Z,12.346,", lines[1]);
        Assert.Equal("2.000,2026-10-18T12:00:02.000Z,12.346,61", lines[2]);
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);   // BOM なし
    }

    [Fact]
    public void Csv_uses_period_decimals_whatever_the_regional_settings_are()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string path = Path.Combine(_dir, "de.csv");
            var row = new SampleRow(new[] { "cpu.load_pct" });
            row.Set("cpu.load_pct", 50.5);
            using (var w = new CsvStream.Writer(path, new[] { "cpu.load_pct" })) w.WriteRow(1.5, DateTime.UtcNow, row);
            Assert.Contains(",50.5", File.ReadAllLines(path)[1]);
            Assert.StartsWith("1.500,", File.ReadAllLines(path)[1]);
            Assert.Equal(50.5, CsvStream.Read(path).Get("cpu.load_pct")![0]);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Csv_reader_skips_truncated_and_backwards_rows_and_folds_per_processor_columns()
    {
        string path = Path.Combine(_dir, "r.csv");
        File.WriteAllText(path, string.Join("\n",
            "t_s,utc,cpu.load_pct,cpu.lp0.load_pct,cpu.lp1.load_pct,cpu.lp0.clock_mhz,gpu0.temp_c",
            "1.000,2026-10-18T12:00:01.000Z,10,100,0,4000,50",
            "2.000,2026-10-18T12:00:02.000Z,20,80,0,4100,",
            "1.500,2026-10-18T12:00:02.500Z,99,99,99,99,99",    // 時間が戻る行
            "3.000,2026-10-18T12:00:03.000Z,30,60,10,4200,52",
            "4.000,2026-10-18T12:00:04.000Z,40,x,x,x,x,x,x",    // 列が多すぎる
            "5.000,2026-10-18T12:00:05.000Z,50,10"));           // 途中で切れた最後の行
        var s = CsvStream.Read(path);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, s.T);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, s.Get("cpu.load_pct"));
        Assert.True(double.IsNaN(s.Get("gpu0.temp_c")![1]));
        Assert.Null(s.Get("cpu.lp0.load_pct"));        // 論理プロセッサごとの列は持たず、平均だけ
        Assert.Null(s.Get("cpu.lp0.clock_mhz"));
        Assert.Equal(2, s.LogicalProcessorAvgLoad.Length);
        Assert.Equal(80, s.LogicalProcessorAvgLoad[0], 6);
        Assert.Equal(10.0 / 3, s.LogicalProcessorAvgLoad[1], 6);
        Assert.Throws<FormatException>(() =>
        {
            string bad = Path.Combine(_dir, "bad.csv");
            File.WriteAllText(bad, "time,value\n1,2\n");
            CsvStream.Read(bad);
        });
    }

    [Fact]
    public void Loading_a_run_rebuilds_the_summary_from_the_csv_and_marks_an_unfinished_run_interrupted()
    {
        var s = FullSession();
        s.Status = "recording";
        s.EndUtc = null;
        s.Columns.Clear();
        s.Columns.AddRange(new[] { "t_s", "utc", "cpu.load_pct" });
        File.WriteAllText(RunStore.JsonPath(_dir, s.RunId), SessionJson.Write(s));
        var row = new SampleRow(new[] { "cpu.load_pct" });
        using (var w = new CsvStream.Writer(RunStore.CsvPath(_dir, s.RunId), new[] { "cpu.load_pct" }))
            for (int i = 1; i <= 70; i++) { row.Set("cpu.load_pct", i <= 35 ? 10 : 90); w.WriteRow(i, s.StartUtc.AddSeconds(i), row); }

        var (session, series, summary) = RunStore.Load(RunStore.JsonPath(_dir, s.RunId));
        Assert.Equal("interrupted", session.Status);
        Assert.Equal("app-closed", session.StopReason);
        Assert.Equal(70, series.Count);
        Assert.Equal(70, summary.Samples);
        Assert.Equal(90, summary.Metric("cpu.load_pct")!.Max);
        Assert.Contains(summary.Notes, n => n.Code == "not-completed");

        var list = RunStore.List(_dir);
        Assert.Equal(s.RunId, Assert.Single(list).RunId);
    }

    [Fact]
    public void Loading_a_run_whose_csv_is_missing_says_so()
    {
        var s = FullSession();
        File.WriteAllText(RunStore.JsonPath(_dir, s.RunId), SessionJson.Write(s));
        Assert.Throws<FileNotFoundException>(() => RunStore.Load(RunStore.JsonPath(_dir, s.RunId)));
    }

    [Fact]
    public void Default_folder_lives_under_documents_parts_life_runs()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PartsLife", "runs");
        Assert.Equal(expected, RunStore.DefaultFolder());
        Assert.Equal(Path.Combine(_dir, "run-abcd2345efgh6789.json"), RunStore.JsonPath(_dir, "abcd2345efgh6789"));
        Assert.Equal(Path.Combine(_dir, "sensors-abcd2345efgh6789.csv"), RunStore.CsvPath(_dir, "abcd2345efgh6789"));
    }
}
