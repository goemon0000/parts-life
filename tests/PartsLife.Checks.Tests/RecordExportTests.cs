using System.Text.Json;
using PartsLife.Checks.Recording;
using static PartsLife.Checks.Tests.RecordFixtures;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 「AIに渡す用」の書き出し。**許可した項目だけ**が出ること、個人を特定する物が**どこにも**出ないこと、
/// 紛れ込んだら黙って出さずに止まること、AI への頼みと禁止事項が入っていることを確かめる。
/// </summary>
public class RecordExportTests
{
    // 試験用の「この機械」。実際の PC の名前や利用者名には依らない
    private const string Machine = "DESKTOP-TESTPC";
    private const string User = "testuser";

    private static ExportGuard Guard() => new(new[] { Machine, User, "abcd2345efgh6789" });

    /// <summary>許可した項目の名前（入れ子も含む）。書き出しに出てよい名前はこれだけ。</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "schema", "app", "name", "version", "run", "status", "durationSec", "intervalSec", "samples", "gapSec", "maxGapSec",
        "sustainedWindowSec", "system", "os", "osBuild", "powerPlan", "ramGb", "logicalProcessors", "powerSource",
        "parts", "cpu", "gpu", "id", "vendor", "vramMb", "ram", "capGb", "speedMTs", "type", "count",
        "markers", "kind", "atSec", "readable", "readablePerLogicalProcessor", "notReadable", "key", "state", "reason",
        "summary", "metrics", "unit", "min", "avg", "max", "sustained",
        "limits", "device", "busySeconds", "reasons", "seconds", "secondsWhileBusy",
        "coOccurrences", "startSec", "endSec", "tempFromC", "tempToC", "clockFromMHz", "clockToMHz", "avgLoadPct",
        "spans", "open", "median",
        "perLogicalProcessor", "avgLoadOfBusiest", "avgLoadOfIdlest", "avgLoadMean", "countWithAvgLoadAtLeast50",
        "timeline", "bucketSamples", "series", "notes", "code", "subject", "text",
    };

    private static (RunSession Session, RunSummary Summary, SeriesSet Series) Fixture(Action<RunSession>? tweak = null)
    {
        var caps = new List<MetricStatus>(GpuCaps())
        {
            Missing("cpu.temp_c", Availability.NeedsExtension, Reasons.NeedsDriver),
            Missing("gpu1.temp_c", Availability.Unavailable, Reasons.NoAdlx),
        };
        var session = Session(caps);
        session.Build = new BuildSnapshot("PCB1.secretcode", BuildJson);
        session.Gpus.Add(new GpuDescriptor(0, "NVIDIA GeForce RTX 5070", "nvidia", 11943));
        session.Markers.Add(new RunMarker(MarkerKind.BenchStart, 20, session.StartUtc));
        session.Markers.Add(new RunMarker(MarkerKind.BenchEnd, 280, session.StartUtc));
        tweak?.Invoke(session);
        var series = ThermalRamp();
        return (session, Summarizer.Summarize(series, session), series);
    }

    private static JsonDocument DataOf(string text)
    {
        const string open = "```json\n", close = "\n```";
        int a = text.IndexOf(open, StringComparison.Ordinal) + open.Length;
        int b = text.LastIndexOf(close, StringComparison.Ordinal);
        return JsonDocument.Parse(text[a..b]);
    }

    private static void CollectNames(JsonElement e, HashSet<string> into, string parent = "")
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                // timeline.series の名前は項目の名前（cpu.load_pct など）。別に確かめる
                if (parent != "series") into.Add(p.Name);
                CollectNames(p.Value, into, p.Name);
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) CollectNames(x, into, parent);
    }

    // =====================================================================
    // 許可した項目だけ
    // =====================================================================

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Only_allowlisted_field_names_appear_and_the_top_level_is_fixed(string lang)
    {
        var (s, sum, series) = Fixture();
        var r = AiExport.Build(s, sum, series, lang, Guard());
        using var doc = DataOf(r.Text);

        Assert.Equal(new[]
        {
            "schema", "app", "run", "system", "parts", "markers", "readable", "readablePerLogicalProcessor",
            "notReadable", "summary", "timeline", "notes",
        }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("partslife-ai-export/1", doc.RootElement.GetProperty("schema").GetString());

        var names = new HashSet<string>();
        CollectNames(doc.RootElement, names);
        Assert.Empty(names.Except(Allowed));

        foreach (var series2 in doc.RootElement.GetProperty("timeline").GetProperty("series").EnumerateObject())
            Assert.Matches(@"^(cpu|gpu\d+)\.[a-z_]+$", series2.Name);
    }

    [Fact]
    public void Forbidden_fields_never_appear_as_names_or_values()
    {
        var (s, sum, series) = Fixture();
        string text = AiExport.Build(s, sum, series, "ja", Guard()).Text;
        using var doc = DataOf(text);
        var names = new HashSet<string>();
        CollectNames(doc.RootElement, names);

        foreach (var forbidden in new[]
                 { "serial", "serialNumber", "userName", "user", "machineName", "computerName", "host", "hostname", "path", "file",
                   "folder", "process", "processName", "runId", "startUtc", "endUtc", "utc", "build", "config", "mfr", "note", "label", "comment" })
            Assert.DoesNotContain(forbidden, names);

        // 値の側: 構成コード・実行の ID・記録のファイル名・パスの形・部品の型番のうち不要な物（SSD・メモリの型番とメーカー）
        Assert.DoesNotContain("PCB1.", text);
        Assert.DoesNotContain("secretcode", text);
        Assert.DoesNotContain(s.RunId, text);
        Assert.DoesNotContain("sensors-", text);
        Assert.DoesNotContain("run-", text);
        Assert.DoesNotContain("Some SSD 2TB", text);
        Assert.DoesNotContain("CMK64GX5M2B5600Z40", text);
        Assert.DoesNotContain("Corsair", text);
        Assert.DoesNotContain(":\\", text);
        Assert.DoesNotContain("2026-10-18", text);     // 日付（UTC を含む）は入れない。時刻は記録の開始からの秒だけ
    }

    [Fact]
    public void What_is_supposed_to_be_there_is_there()
    {
        var (s, sum, series) = Fixture();
        using var doc = DataOf(AiExport.Build(s, sum, series, "en", Guard()).Text);
        var root = doc.RootElement;
        Assert.Equal("Windows 11 Pro", root.GetProperty("system").GetProperty("os").GetString());
        Assert.Equal("Test CPU 9000X 16-Core Processor", root.GetProperty("parts").GetProperty("cpu")[0].GetString());
        var gpu = root.GetProperty("parts").GetProperty("gpu")[0];
        Assert.Equal("NVIDIA GeForce RTX 5070", gpu.GetProperty("name").GetString());
        var ram = root.GetProperty("parts").GetProperty("ram")[0];
        Assert.Equal(32, ram.GetProperty("capGb").GetInt32());
        Assert.Equal(5600, ram.GetProperty("speedMTs").GetInt32());
        Assert.Equal("DDR5", ram.GetProperty("type").GetString());
        Assert.Equal(2, ram.GetProperty("count").GetInt32());
        Assert.Equal("bench-start", root.GetProperty("markers")[0].GetProperty("kind").GetString());
        Assert.Equal(20, root.GetProperty("markers")[0].GetProperty("atSec").GetDouble());
        Assert.Contains(root.GetProperty("readable").EnumerateArray(), x => x.GetString() == "gpu0.temp_c");
        var nr = root.GetProperty("notReadable").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "cpu.temp_c");
        Assert.Equal("needs-extension", nr.GetProperty("state").GetString());
        Assert.Equal(80, root.GetProperty("summary").GetProperty("metrics").EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == "gpu0.temp_c").GetProperty("max").GetDouble());
        Assert.Single(root.GetProperty("summary").GetProperty("coOccurrences").EnumerateArray());
        // 注記は文つきで 1 回だけ（要約の中には入れない）。読めない項目は notReadable にあるので注記には重ねない
        Assert.False(root.GetProperty("summary").TryGetProperty("notes", out _));
        Assert.DoesNotContain(root.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == "unavailable");
    }

    [Fact]
    public void Storage_names_and_memory_part_numbers_are_left_out_but_memory_layout_stays()
    {
        var (s, sum, series) = Fixture();
        string text = AiExport.Build(s, sum, series, "ja", Guard()).Text;
        Assert.DoesNotContain("nvme", text);   // SSD の欄ごと入れない
        Assert.Contains("\"capGb\":32", text);
    }

    // =====================================================================
    // 紛れ込んだ物
    // =====================================================================

    [Fact]
    public void Names_that_contain_the_pc_name_user_name_a_path_or_a_serial_are_withheld_not_exported()
    {
        var (s, sum, series) = Fixture(session =>
        {
            session.System = session.System with { OsName = $"Windows 11 Pro of {Machine}" };
            session.Gpus[0] = new GpuDescriptor(0, $"GeForce {User} edition", "nvidia", 11943);
            session.Gpus.Add(new GpuDescriptor(1, "5CD1234567890ABCDEF12345", "amd", 512));        // シリアルに見える名前
            session.Gpus.Add(new GpuDescriptor(2, @"C:\Users\someone\gpu.inf", "other", 0));        // パスの形
            session.Build = new BuildSnapshot("PCB1.x",
                "{\"v\":1,\"p\":{\"cpu\":[{\"n\":\"Intel " + User + " CPU\"},{\"n\":\"Real CPU 5000\"}],"
                + "\"serial\":\"WD-WX12345678901234\",\"user\":\"" + User + "\",\"path\":\"C:\\\\Users\\\\" + User + "\\\\x\"}}");
        });
        var r = AiExport.Build(s, sum, series, "ja", Guard());

        foreach (var bad in new[] { Machine, User, "5CD1234567890ABCDEF12345", @"C:\Users", "WD-WX1234", "someone", "gpu.inf" })
            Assert.DoesNotContain(bad, r.Text, StringComparison.OrdinalIgnoreCase);
        using var doc = DataOf(r.Text);
        Assert.Equal(new[] { "Real CPU 5000" }, doc.RootElement.GetProperty("parts").GetProperty("cpu").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("Windows", doc.RootElement.GetProperty("system").GetProperty("os").GetString());
        Assert.Equal(5, r.WithheldNames);   // OS・GPU 名 3 つ（利用者名入り・シリアル風・パス）・CPU 1 つ
        Assert.Contains(doc.RootElement.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == "name-withheld");
    }

    [Fact]
    public void Anything_that_slips_through_stops_the_export_instead_of_being_written()
    {
        // 項目の名前に利用者名が入っている（許可した経路を通らない場所）。全文の最後の確かめで止まる
        var (s, sum, series) = Fixture(session => session.Capabilities.Add(Missing("gpu0.temp_c_" + User, Availability.Unavailable, Reasons.Failed)));
        Assert.Throws<ExportBlockedException>(() => AiExport.Build(s, sum, series, "ja", Guard()));

        // 実行の ID が紛れ込んでも止まる
        var (s2, sum2, series2) = Fixture(session => session.Capabilities.Add(Missing("gpu0." + session.RunId, Availability.Unavailable, Reasons.Failed)));
        Assert.Throws<ExportBlockedException>(() => AiExport.Build(s2, sum2, series2, "en", Guard()));

        // パスの形でも止まる
        var guard = Guard();
        Assert.Throws<ExportBlockedException>(() => guard.AssertClean(@"see C:\Users\x\file.txt"));
        Assert.Throws<ExportBlockedException>(() => guard.AssertClean(@"\\server\share"));
        guard.AssertClean("a perfectly fine sentence about 5600 MT/s");
    }

    [Fact]
    public void The_real_machine_and_user_names_are_guarded_by_default()
    {
        var g = ExportGuard.Current("run123456789");
        Assert.True(g.ContainsForbidden("x " + Environment.MachineName + " y"));
        Assert.True(g.ContainsForbidden("hello " + Environment.UserName.ToLowerInvariant() + " world"));   // 大文字小文字は問わない
        Assert.Null(g.Clean("My " + Environment.MachineName + " GPU"));
        Assert.True(g.ContainsForbidden("run123456789"));
        Assert.Equal("NVIDIA GeForce RTX 5070", g.Clean("  NVIDIA   GeForce RTX 5070 "));
    }

    [Fact]
    public void Short_user_names_only_match_as_whole_words()
    {
        var g = new ExportGuard(new[] { "bob" });
        Assert.NotNull(g.Clean("Bobcat 9000"));          // 単語の一部は止めない（何でも止まってしまうため）
        Assert.Null(g.Clean("PC of bob"));
        Assert.Null(g.Clean("Bob's PC"));
    }

    // =====================================================================
    // AI への頼みと禁止事項
    // =====================================================================

    [Fact]
    public void Japanese_text_asks_for_three_parts_and_forbids_diagnosis_and_voltage_advice()
    {
        var (s, sum, series) = Fixture();
        string t = AiExport.Build(s, sum, series, "ja", Guard()).Text;
        string head = t[..t.IndexOf("```json", StringComparison.Ordinal)];
        foreach (var must in new[]
                 { "観測", "仮説", "追加で確認すること", "原因」ではありません", "推測で埋めていません", "測っていません",
                   "断定しないでください", "電圧が安全か危険かを保証", "保護機能を切る", "オーバークロック", "データに無いこと" })
            Assert.Contains(must, head);
        Assert.Contains("`gpuN.temp_c`", head);          // 項目の意味の一覧
        Assert.Contains("```json", t);
    }

    [Fact]
    public void English_text_asks_for_three_parts_and_forbids_diagnosis_and_voltage_advice()
    {
        var (s, sum, series) = Fixture();
        string t = AiExport.Build(s, sum, series, "en", Guard()).Text;
        string head = t[..t.IndexOf("```json", StringComparison.Ordinal)];
        foreach (var must in new[]
                 { "Observations", "Hypotheses", "Extra checks", "not \"caused by\"", "not** filled in by guessing", "not measured",
                   "broken or failing", "guarantee that a voltage is safe or unsafe", "disabling protections", "overclocking", "not in the data" })
            Assert.Contains(must, head);
    }

    [Fact]
    public void The_field_dictionary_lists_only_fields_that_were_actually_read()
    {
        var (s, sum, series) = Fixture();
        s.Capabilities.RemoveAll(c => c.Key == "gpu0.power_w");
        string head = AiExport.Build(s, sum, series, "en", Guard()).Text;
        head = head[..head.IndexOf("```json", StringComparison.Ordinal)];
        Assert.Contains("`gpuN.temp_c`", head);
        Assert.DoesNotContain("`gpuN.power_w`", head);
        Assert.DoesNotContain("`cpu.temp_c`", head);       // 読めない項目の意味は説明しない
    }

    // =====================================================================
    // 時間の変化の間引き
    // =====================================================================

    [Fact]
    public void Timeline_is_thinned_to_at_most_48_points_and_skips_idle_gpus()
    {
        var t = Enumerable.Range(1, 3600).Select(i => (double)i).ToArray();
        var cols = new Dictionary<string, double[]>
        {
            ["cpu.load_pct"] = t.Select(_ => 40.0).ToArray(),
            ["gpu0.util_pct"] = t.Select(_ => 99.0).ToArray(),
            ["gpu0.temp_c"] = t.Select(x => 40 + x / 100).ToArray(),
            ["gpu1.util_pct"] = t.Select(_ => 1.0).ToArray(),     // ほとんど使われなかった GPU
            ["gpu1.temp_c"] = t.Select(_ => 30.0).ToArray(),
        };
        var series = new SeriesSet(t, cols);
        var session = Session();
        var sum = Summarizer.Summarize(series, session);
        using var doc = DataOf(AiExport.Build(session, sum, series, "en", Guard()).Text);
        var tl = doc.RootElement.GetProperty("timeline");
        int points = tl.GetProperty("atSec").GetArrayLength();
        Assert.InRange(points, 40, 48);
        var names = tl.GetProperty("series").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("gpu0.temp_c", names);
        Assert.DoesNotContain("gpu1.temp_c", names);
        Assert.DoesNotContain("gpu1.util_pct", names);
        Assert.All(tl.GetProperty("series").EnumerateObject(), p => Assert.Equal(points, p.Value.GetArrayLength()));
    }
}
