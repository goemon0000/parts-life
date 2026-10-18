using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PartsLife.Checks.Recording;

/// <summary>利用者が押せる目印。**自由な文字は受け付けない**（AI に渡す書き出しに自由記述を入れないため）。</summary>
public enum MarkerKind { BenchStart, BenchEnd, StressStart, StressEnd, Note }

public static class MarkerKinds
{
    public static string Code(MarkerKind k) => k switch
    {
        MarkerKind.BenchStart => "bench-start",
        MarkerKind.BenchEnd => "bench-end",
        MarkerKind.StressStart => "stress-start",
        MarkerKind.StressEnd => "stress-end",
        _ => "mark",
    };

    public static bool TryParse(string? code, out MarkerKind kind)
    {
        switch (code)
        {
            case "bench-start": kind = MarkerKind.BenchStart; return true;
            case "bench-end": kind = MarkerKind.BenchEnd; return true;
            case "stress-start": kind = MarkerKind.StressStart; return true;
            case "stress-end": kind = MarkerKind.StressEnd; return true;
            case "mark": kind = MarkerKind.Note; return true;
            default: kind = MarkerKind.Note; return false;
        }
    }
}

/// <param name="TSec">記録の開始からの秒（単調な時計）。記録の列 `t_s` と同じ物差し。</param>
public sealed record RunMarker(MarkerKind Kind, double TSec, DateTime Utc);

/// <summary>実行の ID。ランダムで、推測できず、ファイル名に使える（小文字と数字だけ）。</summary>
public static class RunId
{
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";   // i l o u 0 1 を除いた 30 字（読み間違えない）
    public const int Length = 16;

    public static string New()
    {
        var chars = new char[Length];
        for (int i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>ファイル名やパスに使ってよい形か。**ファイルから読んだ ID は必ずこれを通す**（`..\` などを紛れ込ませない）。</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || id.Length is < 8 or > 40) return false;
        foreach (char c in id) if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9')) return false;
        return true;
    }
}

public sealed class RunStats
{
    public int Samples { get; set; }
    /// <summary>決めた間隔より大きく空いた時間の合計（秒）。</summary>
    public double GapSec { get; set; }
    public double MaxGapSec { get; set; }
    /// <summary>読み口の名前 → 見切った回数。</summary>
    public Dictionary<string, int> SourceTimeouts { get; set; } = new();
}

/// <summary>機械の素性のうち、解析に効く物だけ。PC の名前・利用者名・シリアルは持つ場所が無い。</summary>
public sealed record SystemSnapshot(string OsName, int OsBuild, string PowerPlan, long RamMb, int LogicalProcessors);

/// <summary>記録の先頭に残す構成（PCB1 の構成コードと、その中身の JSON）。読めなければ null で、記録は続ける。</summary>
public sealed record BuildSnapshot(string Code, string Json);

/// <summary>
/// 1 回の記録。`run-&lt;ID&gt;.json` の中身そのもの。**形を変えるときは <see cref="FormatVersion"/> を上げる**
/// （受け手のブラウザ側と同時に作る約束）。
/// </summary>
public sealed class RunSession
{
    public const int FormatVersion = 1;
    public const string FormatName = "partslife-run";

    public string AppVersion { get; set; } = "";
    public string RunId { get; set; } = "";
    /// <summary>"recording"（記録中・または途中で落ちた）/ "completed"（正常に止めた）/ "interrupted"（読み取りの失敗などで止まった）</summary>
    public string Status { get; set; } = "recording";
    public string StopReason { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public SystemSnapshot System { get; set; } = new("", 0, "unknown", 0, 0);
    public BuildSnapshot? Build { get; set; }
    public List<GpuDescriptor> Gpus { get; set; } = new();
    public List<MetricStatus> Capabilities { get; set; } = new();
    public string CsvFile { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<RunMarker> Markers { get; set; } = new();
    public RunStats Stats { get; set; } = new();
    /// <summary>止めたあとに入る要約（読むときは捨てて、CSV から計算し直す。規則を直せば古い記録の要約も良くなる）。</summary>
    public RunSummary? Summary { get; set; }
}

/// <summary>`run-&lt;ID&gt;.json` の読み書き。手で書く（Utf8JsonWriter）。このアセンブリは NativeAOT の補助 exe にも入るので、リフレクション頼みの直列化は使わない。</summary>
public static class SessionJson
{
    internal static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string StateCode(Availability a) => a switch
    {
        Availability.Available => "available",
        Availability.NeedsExtension => "needs-extension",
        _ => "unavailable",
    };

    public static Availability ParseState(string? s) => s switch
    {
        "available" => Availability.Available,
        "needs-extension" => Availability.NeedsExtension,
        _ => Availability.Unavailable,
    };

    public static string Write(RunSession s)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, Encoder = Encoder }))
        {
            w.WriteStartObject();
            w.WriteString("format", RunSession.FormatName);
            w.WriteNumber("formatVersion", RunSession.FormatVersion);
            w.WriteStartObject("app");
            w.WriteString("name", "PartsLife");
            w.WriteString("version", s.AppVersion);
            w.WriteEndObject();
            w.WriteString("runId", s.RunId);
            w.WriteString("status", s.Status);
            w.WriteString("stopReason", s.StopReason);
            w.WriteString("startUtc", Utc(s.StartUtc));
            if (s.EndUtc is { } end) w.WriteString("endUtc", Utc(end)); else w.WriteNull("endUtc");
            w.WriteNumber("intervalMs", s.IntervalMs);

            w.WriteStartObject("system");
            w.WriteString("os", s.System.OsName);
            w.WriteNumber("osBuild", s.System.OsBuild);
            w.WriteString("powerPlan", s.System.PowerPlan);
            w.WriteNumber("ramMb", s.System.RamMb);
            w.WriteNumber("logicalProcessors", s.System.LogicalProcessors);
            w.WriteEndObject();

            if (s.Build is { } b)
            {
                w.WriteStartObject("build");
                w.WriteString("code", b.Code);
                w.WritePropertyName("config");
                w.WriteRawValue(b.Json, skipInputValidation: false);
                w.WriteEndObject();
            }
            else w.WriteNull("build");

            w.WriteStartArray("gpus");
            foreach (var g in s.Gpus)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id);
                w.WriteString("name", g.Name);
                w.WriteString("vendor", g.Vendor);
                w.WriteNumber("vramMb", g.VramMb);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("capabilities");
            foreach (var c in s.Capabilities)
            {
                w.WriteStartObject();
                w.WriteString("key", c.Key);
                w.WriteString("unit", c.Unit);
                w.WriteString("source", c.Source);
                w.WriteString("state", StateCode(c.State));
                w.WriteString("reason", c.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("stream");
            w.WriteString("file", s.CsvFile);
            w.WriteString("format", "csv");
            w.WriteString("delimiter", ",");
            w.WriteString("monotonicColumn", "t_s");
            w.WriteString("utcColumn", "utc");
            w.WriteStartArray("columns");
            foreach (var c in s.Columns) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("markers");
            foreach (var m in s.Markers)
            {
                w.WriteStartObject();
                w.WriteString("kind", MarkerKinds.Code(m.Kind));
                w.WriteNumber("tSec", Math.Round(m.TSec, 3));
                w.WriteString("utc", Utc(m.Utc));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("stats");
            w.WriteNumber("samples", s.Stats.Samples);
            w.WriteNumber("gapSec", Math.Round(s.Stats.GapSec, 3));
            w.WriteNumber("maxGapSec", Math.Round(s.Stats.MaxGapSec, 3));
            w.WriteStartObject("sourceTimeouts");
            foreach (var (k, v) in s.Stats.SourceTimeouts.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteNumber(k, v);
            w.WriteEndObject();
            w.WriteEndObject();

            if (s.Summary is { } sum)
            {
                w.WritePropertyName("summary");
                SummaryJson.Write(w, sum);
            }
            else w.WriteNull("summary");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>読む。形が違う・版が新しすぎるときは <see cref="FormatException"/>。要約は読まない。</summary>
    public static RunSession Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "format") != RunSession.FormatName)
                throw new FormatException("partslife-run の形ではありません");
            int ver = Int(r, "formatVersion");
            if (ver < 1 || ver > RunSession.FormatVersion)
                throw new FormatException($"formatVersion {ver} は読めません（この版は {RunSession.FormatVersion} まで）");
            string id = Str(r, "runId");
            if (!RunId.IsValid(id)) throw new FormatException("runId が不正です");

            var s = new RunSession
            {
                RunId = id,
                AppVersion = r.TryGetProperty("app", out var app) ? Str(app, "version") : "",
                Status = Str(r, "status"),
                StopReason = Str(r, "stopReason"),
                StartUtc = Time(r, "startUtc") ?? throw new FormatException("startUtc がありません"),
                EndUtc = Time(r, "endUtc"),
                IntervalMs = Math.Max(100, Int(r, "intervalMs", 1000)),
            };
            if (r.TryGetProperty("system", out var sys) && sys.ValueKind == JsonValueKind.Object)
                s.System = new SystemSnapshot(Str(sys, "os"), Int(sys, "osBuild"), Str(sys, "powerPlan", "unknown"),
                                              Long(sys, "ramMb"), Int(sys, "logicalProcessors"));
            if (r.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object
                && build.TryGetProperty("config", out var cfg))
                s.Build = new BuildSnapshot(Str(build, "code"), cfg.GetRawText());
            if (r.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
                foreach (var g in gpus.EnumerateArray())
                {
                    string gid = Str(g, "id");
                    if (!gid.StartsWith("gpu", StringComparison.Ordinal)
                        || !int.TryParse(gid.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int gi)) continue;
                    s.Gpus.Add(new GpuDescriptor(gi, Str(g, "name"), Str(g, "vendor"), Long(g, "vramMb")));
                }
            if (r.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                foreach (var c in caps.EnumerateArray())
                    s.Capabilities.Add(new MetricStatus(Str(c, "key"), Str(c, "unit"), Str(c, "source"), ParseState(Str(c, "state")), Str(c, "reason")));
            if (r.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object)
            {
                s.CsvFile = Str(stream, "file");
                if (stream.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                    foreach (var c in cols.EnumerateArray()) s.Columns.Add(c.GetString() ?? "");
            }
            if (r.TryGetProperty("markers", out var marks) && marks.ValueKind == JsonValueKind.Array)
                foreach (var m in marks.EnumerateArray())
                    if (MarkerKinds.TryParse(Str(m, "kind"), out var kind))
                        s.Markers.Add(new RunMarker(kind, Dbl(m, "tSec"), Time(m, "utc") ?? s.StartUtc));
            if (r.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                s.Stats.Samples = Int(st, "samples");
                s.Stats.GapSec = Dbl(st, "gapSec");
                s.Stats.MaxGapSec = Dbl(st, "maxGapSec");
                if (st.TryGetProperty("sourceTimeouts", out var to) && to.ValueKind == JsonValueKind.Object)
                    foreach (var p in to.EnumerateObject()) s.Stats.SourceTimeouts[p.Name] = p.Value.TryGetInt32(out int n) ? n : 0;
            }
            return s;
        }
        catch (JsonException ex) { throw new FormatException(ex.Message, ex); }
        catch (InvalidOperationException ex) { throw new FormatException(ex.Message, ex); }
    }

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement e, string name, int fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : fallback;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : 0;

    private static DateTime? Time(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
