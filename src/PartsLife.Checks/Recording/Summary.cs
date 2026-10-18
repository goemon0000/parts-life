using System.Text.Json;

namespace PartsLife.Checks.Recording;

/// <summary>
/// 項目 1 つの要約。<paramref name="Sustained"/> は「持続した値」: 負荷が高かった最後の N 分の中央値
/// （N は <see cref="RunSummary.SustainedWindowSec"/>）。負荷が高い時間が見つからなければ null。
/// </summary>
public sealed record MetricSummary(string Key, string Unit, int Samples, double Min, double Avg, double Max, double? Sustained);

/// <summary>制限の理由 1 つ。<paramref name="SecondsWhileBusy"/> は、そのうち負荷が高かった間の秒数。</summary>
public sealed record LimitSeconds(string Name, double Seconds, double SecondsWhileBusy);

/// <summary>
/// 制限の印。GPU は NVIDIA のドライバが返す「クロックを下げている理由」の bit を名前にしたもの。
/// CPU は OS が報告する性能の制限（% Performance Limit が 100 未満・Performance Limit Flags が立っている）。
/// **印が立っていたこと**を言うだけで、それが故障や不具合かは言わない（電力上限での制限は、負荷が高ければ普通のこと）。
/// </summary>
public sealed record LimitSummary(string Device, int Samples, double BusySeconds, IReadOnlyList<LimitSeconds> Reasons);

/// <summary>
/// 温度が上がりながらクロックが下がっていた時間帯。**同じ時間帯に起きたというだけで、原因は言えない。**
/// （温度が上がったから下がったのか、電力や負荷の変化で下がったのかは、この記録からは分からない。）
/// </summary>
public sealed record CoOccurrence(string Device, double StartSec, double EndSec, double TempFromC, double TempToC,
                                  double ClockFromMHz, double ClockToMHz, double AvgLoadPct);

public sealed record SpanMetric(string Key, string Unit, double Min, double Avg, double Max, double Median);

/// <summary>目印で区切った区間（ベンチ開始〜終了、ストレス開始〜終了）の要約。終わりの印が無ければ記録の終わりまで（<paramref name="Open"/>）。</summary>
public sealed record SpanSummary(string Kind, double StartSec, double EndSec, bool Open, IReadOnlyList<SpanMetric> Metrics);

/// <summary>論理プロセッサごとの平均使用率の散らばり。1 つに偏っている（単一スレッド）のか、全体に広がっているのかが分かる。</summary>
public sealed record PerCoreSummary(int LogicalProcessors, double AvgOfMax, double AvgOfMin, double AvgOfMean, int ActiveCount);

/// <summary>要約に添える注記。<paramref name="Code"/> と <paramref name="Subject"/> は言語に依らない印。文にするのは <see cref="RecordText"/>。</summary>
public sealed record SummaryNote(string Code, string Subject, string Detail = "");

public sealed class RunSummary
{
    public double DurationSec { get; init; }
    public int Samples { get; init; }
    public double IntervalSec { get; init; }
    public double GapSec { get; init; }
    public double MaxGapSec { get; init; }
    /// <summary>「持続した値」を出した窓の長さ。0 なら出せなかった。</summary>
    public double SustainedWindowSec { get; init; }
    public IReadOnlyList<MetricSummary> Metrics { get; init; } = Array.Empty<MetricSummary>();
    public IReadOnlyList<LimitSummary> Limits { get; init; } = Array.Empty<LimitSummary>();
    public IReadOnlyList<CoOccurrence> CoOccurrences { get; init; } = Array.Empty<CoOccurrence>();
    public IReadOnlyList<SpanSummary> Spans { get; init; } = Array.Empty<SpanSummary>();
    public PerCoreSummary? PerCore { get; init; }
    public IReadOnlyList<SummaryNote> Notes { get; init; } = Array.Empty<SummaryNote>();

    public MetricSummary? Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key);
}

/// <summary>要約の JSON（記録ファイルの `summary` と、AI に渡す書き出しで同じ形）。言語に依らない。個人の物は持つ場所が無い。</summary>
public static class SummaryJson
{
    public static double R(double v, int digits = 2) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    private static void Num(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, R(v)); else w.WriteNull(name);
    }

    /// <param name="includeNotes">AI に渡す書き出しでは、注記を文つきで別に載せるので false（同じ内容を 2 回書かない）。</param>
    public static void Write(Utf8JsonWriter w, RunSummary s, bool includeNotes = true)
    {
        w.WriteStartObject();
        Num(w, "durationSec", s.DurationSec);
        w.WriteNumber("samples", s.Samples);
        Num(w, "intervalSec", s.IntervalSec);
        Num(w, "gapSec", s.GapSec);
        Num(w, "maxGapSec", s.MaxGapSec);
        Num(w, "sustainedWindowSec", s.SustainedWindowSec);

        w.WriteStartArray("metrics");
        foreach (var m in s.Metrics)
        {
            w.WriteStartObject();
            w.WriteString("key", m.Key);
            w.WriteString("unit", m.Unit);
            w.WriteNumber("samples", m.Samples);
            Num(w, "min", m.Min);
            Num(w, "avg", m.Avg);
            Num(w, "max", m.Max);
            if (m.Sustained is { } v) Num(w, "sustained", v); else w.WriteNull("sustained");
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("limits");
        foreach (var l in s.Limits)
        {
            w.WriteStartObject();
            w.WriteString("device", l.Device);
            w.WriteNumber("samples", l.Samples);
            Num(w, "busySeconds", l.BusySeconds);
            w.WriteStartArray("reasons");
            foreach (var r in l.Reasons)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                Num(w, "seconds", r.Seconds);
                Num(w, "secondsWhileBusy", r.SecondsWhileBusy);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("coOccurrences");
        foreach (var c in s.CoOccurrences)
        {
            w.WriteStartObject();
            w.WriteString("device", c.Device);
            Num(w, "startSec", c.StartSec);
            Num(w, "endSec", c.EndSec);
            Num(w, "tempFromC", c.TempFromC);
            Num(w, "tempToC", c.TempToC);
            Num(w, "clockFromMHz", c.ClockFromMHz);
            Num(w, "clockToMHz", c.ClockToMHz);
            Num(w, "avgLoadPct", c.AvgLoadPct);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("spans");
        foreach (var sp in s.Spans)
        {
            w.WriteStartObject();
            w.WriteString("kind", sp.Kind);
            Num(w, "startSec", sp.StartSec);
            Num(w, "endSec", sp.EndSec);
            w.WriteBoolean("open", sp.Open);
            w.WriteStartArray("metrics");
            foreach (var m in sp.Metrics)
            {
                w.WriteStartObject();
                w.WriteString("key", m.Key);
                w.WriteString("unit", m.Unit);
                Num(w, "min", m.Min);
                Num(w, "avg", m.Avg);
                Num(w, "max", m.Max);
                Num(w, "median", m.Median);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (s.PerCore is { } pc)
        {
            w.WriteStartObject("perLogicalProcessor");
            w.WriteNumber("count", pc.LogicalProcessors);
            Num(w, "avgLoadOfBusiest", pc.AvgOfMax);
            Num(w, "avgLoadOfIdlest", pc.AvgOfMin);
            Num(w, "avgLoadMean", pc.AvgOfMean);
            w.WriteNumber("countWithAvgLoadAtLeast50", pc.ActiveCount);
            w.WriteEndObject();
        }
        else w.WriteNull("perLogicalProcessor");

        if (includeNotes)
        {
            w.WriteStartArray("notes");
            foreach (var n in s.Notes)
            {
                w.WriteStartObject();
                w.WriteString("code", n.Code);
                w.WriteString("subject", n.Subject);
                w.WriteString("detail", n.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}
