using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PartsLife.Checks.Build;

namespace PartsLife.Checks.Recording;

/// <summary>書き出しに、入れてはいけない文字列が紛れ込んだ。書き出しを止める（黙って出さない）。</summary>
public sealed class ExportBlockedException : Exception
{
    public ExportBlockedException(string message) : base(message) { }
}

/// <summary>
/// AI に渡す書き出しの最後の守り。**許可した項目だけを書く**作りが本線で、これは「それでも紛れ込んだ」場合の網:
/// PC の名前・利用者名・ユーザーフォルダの名前・実行の ID・ファイルのパスらしい文字列・シリアルらしい文字列。
/// 名前（部品の型番など）は <see cref="Clean"/> を通し、通らない物は書き出さない。出来上がった全文は <see cref="AssertClean"/> で確かめる。
/// </summary>
public sealed partial class ExportGuard
{
    private readonly string[] _forbidden;

    public ExportGuard(IEnumerable<string> forbidden)
    {
        _forbidden = forbidden.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>この機械の PC の名前・利用者名・ユーザーフォルダの名前に、<paramref name="extra"/>（実行の ID など）を足した守り。</summary>
    public static ExportGuard Current(params string[] extra)
    {
        var list = new List<string> { Environment.MachineName, Environment.UserName };
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0) list.Add(Path.GetFileName(profile.TrimEnd('\\', '/')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        list.AddRange(extra);
        return new ExportGuard(list);
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/]|\\\\|(^|[\s""'])/(Users|home)/", RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();

    /// <summary>名前として書き出してよいなら整えた文字列、だめなら null。</summary>
    public string? Clean(string? name, int max = 80)
    {
        string c = BuildRules.CleanName(name, max);
        if (c.Length == 0) return null;
        if (ContainsForbidden(c) || PathLike().IsMatch(c) || BuildRules.LooksLikeSerial(c)) return null;
        return c;
    }

    /// <summary>
    /// 禁止の文字列を含むか。長さ 4 以上は部分一致、3 以下は単語として一致したときだけ
    /// （短い名前が、関係ない単語の一部に当たって何でも止めてしまわないように）。
    /// </summary>
    public bool ContainsForbidden(string text)
    {
        foreach (var f in _forbidden)
        {
            if (f.Length >= 4) { if (text.Contains(f, StringComparison.OrdinalIgnoreCase)) return true; }
            else if (ContainsToken(text, f)) return true;
        }
        return false;
    }

    private static bool ContainsToken(string text, string token)
    {
        int at = 0;
        while ((at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool left = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + token.Length;
            bool right = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (left && right) return true;
            at++;
        }
        return false;
    }

    /// <summary>出来上がった全文を確かめる。禁止の文字列かパスらしい文字列があれば止める。</summary>
    public void AssertClean(string text)
    {
        foreach (var f in _forbidden)
        {
            bool hit = f.Length >= 4 ? text.Contains(f, StringComparison.OrdinalIgnoreCase) : ContainsToken(text, f);
            if (hit) throw new ExportBlockedException("書き出しに個人を特定しうる文字列が含まれていたため止めました。");
        }
        if (PathLike().IsMatch(text)) throw new ExportBlockedException("書き出しにファイルのパスらしい文字列が含まれていたため止めました。");
    }
}

public sealed record AiExportResult(string Text, int WithheldNames);

/// <summary>
/// 「AIに渡す用」の書き出し。**許可した項目だけを、ここで 1 つずつ書く**（記録の見出しをそのまま写さない）。
///
/// 入る物: 版・記録の長さと間隔・OS の名前と版・電源プラン・メモリの量・CPU と GPU の型番・メモリの構成・項目ごとの要約・
/// 制限の印・同時に起きた時間帯・目印の種類と時刻（記録の開始からの秒）・間引いた時間の変化・注記。
/// 入らない物: シリアル番号・PC の名前・利用者名・ファイルのパス・プロセスの名前・自由に書いた文字・実行の ID・日時（UTC を含む）・構成コード（PCB1）。
/// 文書の前半は AI への説明（項目の意味・答え方の頼み・してはいけないこと）、後半が JSON のデータ。
/// </summary>
public static class AiExport
{
    public const string Schema = "partslife-ai-export/1";
    private const int TimelinePoints = 48;

    public static AiExportResult Build(RunSession session, RunSummary summary, SeriesSet series, string lang, ExportGuard? guard = null)
    {
        guard ??= ExportGuard.Current(session.RunId);
        lang = lang == "en" ? "en" : "ja";
        int withheld = 0;
        string? Clean(string? s, int max = 80)
        {
            var c = guard.Clean(s, max);
            if (c is null && !string.IsNullOrWhiteSpace(s)) withheld++;
            return c;
        }

        var readable = session.Capabilities.Where(c => c.IsAvailable && !MetricKeys.IsPerLogicalProcessor(c.Key)).Select(c => c.Key).ToList();
        int lpCount = session.Capabilities.Count(c => c.IsAvailable && c.Key.StartsWith("cpu.lp", StringComparison.Ordinal) && c.Key.EndsWith(".load_pct", StringComparison.Ordinal));

        var data = new StringBuilder();
        data.Append("{\n");
        void Section(string name, Action<Utf8JsonWriter> write, bool last = false)
        {
            data.Append(" \"").Append(name).Append("\": ").Append(Compact(write)).Append(last ? "\n" : ",\n");
        }

        Section("schema", w => w.WriteStringValue(Schema));
        Section("app", w =>
        {
            w.WriteStartObject();
            w.WriteString("name", "PartsLife");
            w.WriteString("version", Clean(session.AppVersion, 20) ?? "");
            w.WriteEndObject();
        });
        Section("run", w =>
        {
            w.WriteStartObject();
            w.WriteString("status", session.Status);
            w.WriteNumber("durationSec", SummaryJson.R(summary.DurationSec, 1));
            w.WriteNumber("intervalSec", SummaryJson.R(summary.IntervalSec, 2));
            w.WriteNumber("samples", summary.Samples);
            w.WriteNumber("gapSec", SummaryJson.R(summary.GapSec, 1));
            w.WriteNumber("sustainedWindowSec", SummaryJson.R(summary.SustainedWindowSec, 0));
            w.WriteEndObject();
        });
        Section("system", w =>
        {
            w.WriteStartObject();
            w.WriteString("os", Clean(session.System.OsName, 60) ?? "Windows");
            w.WriteNumber("osBuild", session.System.OsBuild);
            w.WriteString("powerPlan", session.System.PowerPlan);
            if (session.System.RamMb > 0) w.WriteNumber("ramGb", SummaryJson.R(session.System.RamMb / 1024.0, 1));
            w.WriteNumber("logicalProcessors", session.System.LogicalProcessors);
            w.WriteString("powerSource", PowerSource(series));
            w.WriteEndObject();
        });
        Section("parts", w => WriteParts(w, session, Clean));
        Section("markers", w =>
        {
            w.WriteStartArray();
            foreach (var m in session.Markers)
            {
                w.WriteStartObject();
                w.WriteString("kind", MarkerKinds.Code(m.Kind));
                w.WriteNumber("atSec", SummaryJson.R(m.TSec, 1));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        Section("readable", w =>
        {
            w.WriteStartArray();
            foreach (var k in readable) w.WriteStringValue(k);
            w.WriteEndArray();
        });
        Section("readablePerLogicalProcessor", w => w.WriteNumberValue(lpCount));
        Section("notReadable", w =>
        {
            w.WriteStartArray();
            foreach (var c in session.Capabilities.Where(c => !c.IsAvailable))
            {
                w.WriteStartObject();
                w.WriteString("key", c.Key);
                w.WriteString("state", SessionJson.StateCode(c.State));
                w.WriteString("reason", c.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        Section("summary", w => SummaryJson.Write(w, summary, includeNotes: false));
        Section("timeline", w => WriteTimeline(w, series, summary));
        // 「読めない項目」は notReadable に理由つきで載せたので、注記からは外す（同じことを 2 回書かない）
        var notes = summary.Notes.Where(n => n.Code != "unavailable").ToList();
        if (withheld > 0) notes.Add(new SummaryNote("name-withheld", ""));
        Section("notes", w =>
        {
            w.WriteStartArray();
            foreach (var n in notes)
            {
                w.WriteStartObject();
                w.WriteString("code", n.Code);
                w.WriteString("subject", n.Subject);
                w.WriteString("text", RecordText.Note(n, lang));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, last: true);
        data.Append('}');

        var text = new StringBuilder();
        text.Append(Preamble(lang, readable));
        text.Append("```json\n").Append(data).Append("\n```\n");
        string result = text.ToString();
        guard.AssertClean(result);
        return new AiExportResult(result, withheld);
    }

    private static string Compact(Action<Utf8JsonWriter> write)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = false, Encoder = SessionJson.Encoder }))
            write(w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string PowerSource(SeriesSet s)
    {
        var ac = s.Get(MetricKeys.PowerAc);
        if (ac is null || !Stat.TryStats(ac, out _, out double min, out _, out double max)) return "unknown";
        return min >= 0.5 ? "ac" : max < 0.5 ? "battery" : "mixed";
    }

    private static void WriteParts(Utf8JsonWriter w, RunSession session, Func<string?, int, string?> clean)
    {
        w.WriteStartObject();
        var cpus = new List<string>();
        var rams = new List<(int? Cap, int? Spd, string? Type, int Q)>();
        if (session.Build is { } b)
        {
            try
            {
                using var doc = JsonDocument.Parse(b.Json);
                if (doc.RootElement.TryGetProperty("p", out var p))
                {
                    if (p.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Array)
                        foreach (var c in cpu.EnumerateArray())
                            if (c.TryGetProperty("n", out var n) && clean(n.GetString(), 80) is { } name) cpus.Add(name);
                    if (p.TryGetProperty("ram", out var ram) && ram.ValueKind == JsonValueKind.Array)
                        foreach (var r in ram.EnumerateArray())
                            rams.Add((Num(r, "cap"), Num(r, "spd"), r.TryGetProperty("t", out var t) ? clean(t.GetString(), 8) : null, Num(r, "q") ?? 1));
                }
            }
            catch (JsonException) { /* 構成が読めなければ、部品の欄を空にする */ }
        }
        w.WriteStartArray("cpu");
        foreach (var c in cpus) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteStartArray("gpu");
        foreach (var g in session.Gpus)
        {
            w.WriteStartObject();
            w.WriteString("id", g.Id);
            w.WriteString("name", clean(g.Name, 80) ?? "");
            w.WriteString("vendor", g.Vendor);
            w.WriteNumber("vramMb", g.VramMb);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("ram");
        foreach (var (cap, spd, type, q) in rams)
        {
            w.WriteStartObject();
            if (cap is { } c) w.WriteNumber("capGb", c);
            if (spd is { } s) w.WriteNumber("speedMTs", s);
            if (type is not null) w.WriteString("type", type);
            w.WriteNumber("count", q);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        static int? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
    }

    /// <summary>時間の変化。全体を最大 48 個に間引き（区間の平均）、主な項目だけ。ほとんど使われなかった GPU は入れない。</summary>
    private static void WriteTimeline(Utf8JsonWriter w, SeriesSet s, RunSummary summary)
    {
        w.WriteStartObject();
        if (s.Count == 0) { w.WriteEndObject(); return; }

        var idle = summary.Notes.Where(n => n.Code == "device-idle").Select(n => n.Subject).ToHashSet(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var k in new[] { MetricKeys.CpuLoad, MetricKeys.CpuClock, MetricKeys.CpuPackagePower })
            if (s.Columns.ContainsKey(k)) keys.Add(k);
        foreach (var k in s.Columns.Keys.OrderBy(x => x, StringComparer.Ordinal))
            if (MetricKeys.TryParseGpu(k, out int gi, out string m) && !idle.Contains("gpu" + gi.ToString(CultureInfo.InvariantCulture))
                && m is MetricKeys.Util or MetricKeys.Temp or MetricKeys.Clock or MetricKeys.Power)
                keys.Add(k);

        int size = Math.Max(1, (int)Math.Ceiling(s.Count / (double)TimelinePoints));
        int buckets = (s.Count + size - 1) / size;
        w.WriteNumber("bucketSamples", size);
        w.WriteStartArray("atSec");
        for (int b = 0; b < buckets; b++) w.WriteNumberValue(SummaryJson.R(s.T[b * size], 0));
        w.WriteEndArray();
        w.WriteStartObject("series");
        foreach (var k in keys)
        {
            var v = s.Columns[k];
            int digits = k.EndsWith("clock_mhz", StringComparison.Ordinal) ? 0 : 1;
            w.WriteStartArray(k);
            for (int b = 0; b < buckets; b++)
            {
                int from = b * size, to = Math.Min(s.Count - 1, from + size - 1);
                double mean = Stat.MeanRange(v, from, to);
                if (double.IsNaN(mean)) w.WriteNullValue(); else w.WriteNumberValue(SummaryJson.R(mean, digits));
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>AI への説明。項目の意味・答え方の頼み・してはいけないこと。</summary>
    internal static string Preamble(string lang, IReadOnlyList<string> readable)
    {
        bool en = lang == "en";
        var sb = new StringBuilder();
        if (en)
        {
            sb.Append("# PartsLife test recording (for an AI assistant)\n\n");
            sb.Append("This is a summary made on the user's own PC by the Windows app \"Parts Life\". While the user ran a browser benchmark or stress test, ")
              .Append("it logged PC sensors once per second and summarised them locally. The data below is that summary and a thinned-out timeline; it is **not** the raw per-second log.\n\n");
            sb.Append("## About the data\n");
            sb.Append("- Every value is only what Windows and the graphics driver could actually report. Items that could not be read are listed in `notReadable` with a reason and were **not** filled in by guessing.\n");
            sb.Append("- CPU temperature, voltages and fan speeds are **not measured** by this version (`needs-extension`). \"Not readable\" does not mean \"fine\".\n");
            sb.Append("- \"Happened at the same time\" is not \"caused by\". `coOccurrences` only lists time windows where temperature rose while the clock fell.\n");
            sb.Append("- `sustained` is the median over the last `sustainedWindowSec` seconds during which that device was under high load (5-second average of load >= 50%).\n");
            sb.Append("- `limits` counts the seconds in which the driver or Windows reported a reason for holding clocks back. A software power cap under heavy load is normal.\n");
            sb.Append("- Times are seconds from the start of the recording. No dates, names or identifiers are included.\n\n");
            sb.Append("## Meaning of the fields\n");
            AppendMeanings(sb, readable, lang);
            sb.Append("\n## What I would like from you\n");
            sb.Append("Please answer in three separate parts:\n");
            sb.Append("1. **Observations** - what the data directly shows (quote the numbers).\n");
            sb.Append("2. **Hypotheses** - possible explanations. Give each a confidence (high / medium / low) and the data it rests on. Do not mix them with observations.\n");
            sb.Append("3. **Extra checks** - missing data, what to record next, and how to check it.\n\n");
            sb.Append("## Please do not\n");
            sb.Append("- State that a part is broken or failing. You may describe possibilities.\n");
            sb.Append("- Judge or guarantee that a voltage is safe or unsafe, and do not recommend raising voltages or power limits, disabling protections, or changing overclocking settings.\n");
            sb.Append("- Present anything that is not in the data as fact.\n\n");
            sb.Append("## Data\n");
        }
        else
        {
            sb.Append("# パーツぐらし テスト記録（AI に渡す用）\n\n");
            sb.Append("これは、Windows のソフト「パーツぐらし」が、利用者の PC の中で作った要約です。ブラウザのベンチマークやストレステストの最中に PC のセンサーを 1 秒ごとに記録し、手元で要約しました。")
              .Append("下のデータは、その要約と間引いた時間の変化で、**毎秒の生の記録ではありません。**\n\n");
            sb.Append("## このデータについて\n");
            sb.Append("- 値はすべて、Windows の標準機能とグラフィックスドライバが実際に返せた物だけです。読めなかった項目は `notReadable` に理由つきで書いてあり、**推測で埋めていません。**\n");
            sb.Append("- CPU の温度・電圧・ファン回転数は、この版では**測っていません**（`needs-extension`）。「読めない」ことは「問題ない」ことではありません。\n");
            sb.Append("- 「同時に起きた」ことは「原因」ではありません。`coOccurrences` は、同じ時間帯に温度が上がりクロックが下がっていた、という事実だけを並べたものです。\n");
            sb.Append("- `sustained` は、そのデバイスに高い負荷が掛かっていた（負荷の 5 秒平均が 50% 以上）最後の `sustainedWindowSec` 秒の中央値です。\n");
            sb.Append("- `limits` は、ドライバや Windows が「クロックを下げている理由」を返していた秒数です。負荷が高いときの電力上限（ソフトウェア）による低下は普通のことです。\n");
            sb.Append("- 時刻は記録の開始からの秒です。日付・名前・識別子は入っていません。\n\n");
            sb.Append("## フィールドの意味\n");
            AppendMeanings(sb, readable, lang);
            sb.Append("\n## お願い\n");
            sb.Append("次の 3 つに分けて答えてください。\n");
            sb.Append("1. **観測** — データから直接言えること（数値を引用してください）\n");
            sb.Append("2. **仮説** — 考えられる理由。それぞれに確からしさ（高い／中／低い）と、根拠にしたデータを添えてください。観測とは混ぜないでください\n");
            sb.Append("3. **追加で確認すること** — 足りないデータ、次に取ると良い記録、確認の手順\n\n");
            sb.Append("## してはいけないこと\n");
            sb.Append("- 部品が「故障している」「壊れかけている」と断定しないでください（可能性として述べるのは構いません）。\n");
            sb.Append("- 電圧が安全か危険かを保証・判断しないでください。電圧や電力上限を上げる、保護機能を切る、オーバークロックの設定を変える、といった指示もしないでください。\n");
            sb.Append("- データに無いことを事実のように書かないでください。\n\n");
            sb.Append("## データ\n");
        }
        return sb.ToString();
    }

    private static void AppendMeanings(StringBuilder sb, IReadOnlyList<string> readable, string lang)
    {
        var seenTail = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in readable)
        {
            string m = RecordText.Meaning(key, lang);
            if (m.Length == 0) continue;
            // GPU の同じ項目は 1 行にまとめる（gpu0 と gpu1 で同じ説明を繰り返さない）
            string shown = MetricKeys.TryParseGpu(key, out _, out string tail) ? "gpuN." + tail : key;
            if (!seenTail.Add(shown)) continue;
            sb.Append("- `").Append(shown).Append("`: ").Append(m).Append('\n');
        }
        sb.Append(lang == "en"
            ? "- `cpu.lp<N>.*` (not exported): per-logical-processor values exist in the raw log; here only their spread is summarised in `summary.perLogicalProcessor`.\n"
            : "- `cpu.lp<N>.*`（書き出しには入れていません）: 論理プロセッサごとの値は生の記録にだけあり、ここでは `summary.perLogicalProcessor` にばらつきだけを要約しています。\n");
    }
}
