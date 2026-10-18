using System.Globalization;

namespace PartsLife.Checks.Recording;

/// <summary>NVIDIA のドライバが返す「クロックを下げている理由」（nvmlClocksEventReasons）の bit と名前。</summary>
public static class ThrottleBits
{
    public static readonly (ulong Bit, string Name)[] All =
    {
        (0x1, "gpu-idle"),
        (0x2, "applications-clocks-setting"),
        (0x4, "sw-power-cap"),
        (0x8, "hw-slowdown"),
        (0x10, "sync-boost"),
        (0x20, "sw-thermal-slowdown"),
        (0x40, "hw-thermal-slowdown"),
        (0x80, "hw-power-brake-slowdown"),
        (0x100, "display-clock-setting"),
    };
}

/// <summary>
/// 規則による要約。**純粋な関数**（ファイルにも機械にも触らない）。数字を出し、同時に起きたことを並べ、読めなかったことを書く。
/// **原因は言わない・故障は言わない。** 「温度が上がりながらクロックが下がっていた時間帯」は、同じ時間に起きたというだけ。
///
/// ■ 「持続した値」
/// 負荷が高かった（5 秒の移動平均で 50% 以上）最後の N 分の、負荷が高かった時点だけの中央値。
/// 記録の最後が待機に戻っていても、待機のクロックを「持続」とは呼ばないため。N は記録の長さの 1/3（30 秒〜5 分）。
/// 60 秒に満たない記録では出さない（「短すぎる」と書く）。
/// </summary>
public static class Summarizer
{
    public const double BusyPct = 50;
    public const double IdleMaxPct = 20;
    public const double MinDurationForSustained = 60;
    public const double CoWindowSec = 30;
    public const double CoTempRiseC = 3.0;
    public const double CoClockDropRatio = 0.05;
    public const double CoMinClockMHz = 300;
    private const int MaxCoOccurrences = 6;
    private const int MinBusySamples = 10;

    public static RunSummary Summarize(SeriesSet s, RunSession session)
    {
        double interval = Math.Max(0.1, session.IntervalMs / 1000.0);
        var notes = new List<SummaryNote>();
        int n = s.Count;
        if (n == 0)
        {
            notes.Add(new SummaryNote("no-samples", ""));
            AddUnavailableNotes(session, notes);
            return new RunSummary { IntervalSec = interval, Notes = notes };
        }

        double duration = s.T[^1];
        var (gapSec, maxGap) = Gaps(s.T, interval);

        // --- 負荷の高かった時点（デバイスごと） ---
        var busy = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        var loadOf = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.Get(MetricKeys.CpuLoad) is { } cpuLoad) { busy["cpu"] = BusyFlags(cpuLoad); loadOf["cpu"] = MetricKeys.CpuLoad; }
        foreach (var key in s.Columns.Keys)
            if (MetricKeys.TryParseGpu(key, out int gi, out string m) && m == MetricKeys.Util)
            {
                string dev = "gpu" + gi.ToString(CultureInfo.InvariantCulture);
                busy[dev] = BusyFlags(s.Columns[key]);
                loadOf[dev] = key;
            }

        // --- 持続した値を取る時点 ---
        double window = duration >= MinDurationForSustained ? Math.Clamp(duration / 3, 30, 300) : 0;
        var sustainIdx = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        if (window > 0)
        {
            foreach (var (dev, flags) in busy)
            {
                int last = Array.FindLastIndex(flags, f => f);
                if (last < 0) continue;
                double end = s.T[last];
                var idx = new List<int>();
                for (int i = last; i >= 0 && s.T[i] >= end - window; i--) if (flags[i]) idx.Add(i);
                if (idx.Count >= MinBusySamples) { idx.Reverse(); sustainIdx[dev] = idx; }
            }
        }
        else notes.Add(new SummaryNote("too-short", "", duration.ToString("0", CultureInfo.InvariantCulture)));

        // --- 項目ごとの要約 ---
        var metrics = new List<MetricSummary>();
        foreach (var (key, values) in s.Columns.OrderBy(c => ColumnOrder(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (IsSpecial(key)) continue;
            if (!Stat.TryStats(values, out int cnt, out double min, out double avg, out double max)) continue;
            double? sustained = null;
            string dev = DeviceOf(key);
            if (sustainIdx.TryGetValue(dev, out var idx))
            {
                var picked = new List<double>(idx.Count);
                foreach (int i in idx) if (!double.IsNaN(values[i])) picked.Add(values[i]);
                if (picked.Count > 0) sustained = Stat.Median(picked);
            }
            metrics.Add(new MetricSummary(key, MetricKeys.UnitOf(key), cnt, min, avg, max, sustained));
            if (cnt < n * 0.8) notes.Add(new SummaryNote("sparse", key, (cnt * 100.0 / n).ToString("0", CultureInfo.InvariantCulture)));
        }
        if (window > 0)
            foreach (var dev in busy.Keys.OrderBy(d => d, StringComparer.Ordinal))
                if (!sustainIdx.ContainsKey(dev) && Stat.TryStats(s.Columns[loadOf[dev]], out _, out _, out _, out double peak) && peak >= BusyPct)
                    notes.Add(new SummaryNote("no-sustained-busy", dev));

        // --- 使われなかったデバイス ---
        foreach (var dev in busy.Keys.OrderBy(d => d, StringComparer.Ordinal))
            if (Stat.TryStats(s.Columns[loadOf[dev]], out _, out _, out _, out double peak) && peak < IdleMaxPct)
                notes.Add(new SummaryNote("device-idle", dev, peak.ToString("0", CultureInfo.InvariantCulture)));

        // --- 制限の印 ---
        var limits = new List<LimitSummary>();
        foreach (var dev in busy.Keys.Where(d => d.StartsWith("gpu", StringComparison.Ordinal)).OrderBy(d => d, StringComparer.Ordinal))
            if (s.Get(dev + "." + MetricKeys.Throttle) is { } mask)
                limits.Add(GpuLimits(dev, s, mask, busy[dev], interval));
        if (CpuLimits(s, busy.GetValueOrDefault("cpu"), interval) is { } cpuLimit) limits.Add(cpuLimit);

        // --- 温度が上がりながらクロックが下がっていた時間帯（同時に起きただけ） ---
        var co = new List<CoOccurrence>();
        foreach (var dev in busy.Keys.Where(d => d.StartsWith("gpu", StringComparison.Ordinal)).OrderBy(d => d, StringComparer.Ordinal))
        {
            var temp = s.Get(dev + "." + MetricKeys.Temp);
            var clock = s.Get(dev + "." + MetricKeys.Clock);
            if (temp is null || clock is null) continue;
            co.AddRange(FindCoOccurrences(dev, s.T, temp, clock, s.Get(loadOf[dev])));
        }

        // --- 目印で区切った区間 ---
        var spans = Spans(s, session.Markers, notes);

        // --- 論理プロセッサの偏り ---
        PerCoreSummary? perCore = null;
        if (s.LogicalProcessorAvgLoad.Length > 0)
        {
            var a = s.LogicalProcessorAvgLoad;
            perCore = new PerCoreSummary(a.Length, a.Max(), a.Min(), a.Average(), a.Count(x => x >= 50));
        }

        // --- 注記 ---
        AddUnavailableNotes(session, notes);
        if (s.Get(MetricKeys.PowerAc) is { } ac && Stat.TryStats(ac, out _, out double acMin, out _, out _) && acMin < 0.5)
            notes.Add(new SummaryNote("battery-used", ""));
        if (maxGap > interval * 3 || gapSec > duration * 0.05)
            notes.Add(new SummaryNote("gaps", "", $"{gapSec.ToString("0.#", CultureInfo.InvariantCulture)}/{maxGap.ToString("0.#", CultureInfo.InvariantCulture)}"));
        foreach (var (src, cnt) in session.Stats.SourceTimeouts.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (cnt > 0) notes.Add(new SummaryNote("source-timeouts", src, cnt.ToString(CultureInfo.InvariantCulture)));
        if (session.Status != "completed")
            notes.Add(new SummaryNote("not-completed", session.Status, session.StopReason));
        var cpuClock = metrics.FirstOrDefault(m => m.Key == MetricKeys.CpuClock);
        if (cpuClock?.Sustained is { } sc && sc < cpuClock.Max * 0.95
            && !s.Columns.ContainsKey(MetricKeys.CpuTemp))
            notes.Add(new SummaryNote("cpu-clock-drop-no-temp", "cpu",
                $"{sc.ToString("0", CultureInfo.InvariantCulture)}/{cpuClock.Max.ToString("0", CultureInfo.InvariantCulture)}"));

        return new RunSummary
        {
            DurationSec = duration,
            Samples = n,
            IntervalSec = interval,
            GapSec = gapSec,
            MaxGapSec = maxGap,
            SustainedWindowSec = sustainIdx.Count > 0 ? window : 0,
            Metrics = metrics,
            Limits = limits,
            CoOccurrences = co,
            Spans = spans,
            PerCore = perCore,
            Notes = notes,
        };
    }

    // =====================================================================
    // 部品
    // =====================================================================

    internal static bool IsSpecial(string key) =>
        key.EndsWith("." + MetricKeys.Throttle, StringComparison.Ordinal) || key == MetricKeys.CpuPerfLimitFlags || key == MetricKeys.PowerAc;

    /// <summary>"cpu" / "gpu0" / "mem" / "disk" / "power" / "board"。</summary>
    internal static string DeviceOf(string key)
    {
        int dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }

    private static int ColumnOrder(string key)
    {
        string dev = DeviceOf(key);
        if (dev == "cpu") return 0;
        if (dev.StartsWith("gpu", StringComparison.Ordinal)) return 1;
        return dev switch { "mem" => 2, "disk" => 3, "power" => 4, _ => 5 };
    }

    private static bool[] BusyFlags(double[] load)
    {
        var sm = Stat.TrailingMean(load, 5);
        var f = new bool[sm.Length];
        for (int i = 0; i < f.Length; i++) f[i] = sm[i] >= BusyPct;
        return f;
    }

    private static (double GapSec, double MaxGap) Gaps(double[] t, double interval)
    {
        double gap = 0, max = 0;
        for (int i = 1; i < t.Length; i++)
        {
            double dt = t[i] - t[i - 1];
            max = Math.Max(max, dt);
            if (dt > interval * 1.5) gap += dt - interval;
        }
        return (gap, max);
    }

    private static void AddUnavailableNotes(RunSession session, List<SummaryNote> notes)
    {
        foreach (var c in session.Capabilities)
        {
            if (c.IsAvailable || MetricKeys.IsPerLogicalProcessor(c.Key)) continue;
            notes.Add(new SummaryNote("unavailable", c.Key, SessionJson.StateCode(c.State) + ":" + c.Reason));
        }
    }

    private static LimitSummary GpuLimits(string dev, SeriesSet s, double[] mask, bool[] busy, double interval)
    {
        int valid = 0, busyCount = 0;
        var secs = new double[ThrottleBits.All.Length];
        var secsBusy = new double[ThrottleBits.All.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            if (double.IsNaN(mask[i])) continue;
            valid++;
            if (busy[i]) busyCount++;
            ulong bits = (ulong)mask[i];
            for (int k = 0; k < ThrottleBits.All.Length; k++)
            {
                if ((bits & ThrottleBits.All[k].Bit) == 0) continue;
                secs[k] += interval;
                if (busy[i]) secsBusy[k] += interval;
            }
        }
        var reasons = new List<LimitSeconds>();
        for (int k = 0; k < secs.Length; k++)
            if (secs[k] > 0) reasons.Add(new LimitSeconds(ThrottleBits.All[k].Name, secs[k], secsBusy[k]));
        return new LimitSummary(dev, valid, busyCount * interval, reasons);
    }

    private static LimitSummary? CpuLimits(SeriesSet s, bool[]? busy, double interval)
    {
        var pct = s.Get(MetricKeys.CpuPerfLimit);
        var flags = s.Get(MetricKeys.CpuPerfLimitFlags);
        if (pct is null && flags is null) return null;
        int valid = 0, busyCount = 0;
        double limited = 0, limitedBusy = 0, flagged = 0, flaggedBusy = 0;
        for (int i = 0; i < s.Count; i++)
        {
            bool b = busy is not null && busy[i];
            bool has = false;
            if (pct is not null && !double.IsNaN(pct[i]))
            {
                has = true;
                if (pct[i] < 99.5) { limited += interval; if (b) limitedBusy += interval; }
            }
            if (flags is not null && !double.IsNaN(flags[i]))
            {
                has = true;
                if (flags[i] > 0) { flagged += interval; if (b) flaggedBusy += interval; }
            }
            if (has) { valid++; if (b) busyCount++; }
        }
        var reasons = new List<LimitSeconds>();
        if (limited > 0) reasons.Add(new LimitSeconds("os-performance-limit-below-100", limited, limitedBusy));
        if (flagged > 0) reasons.Add(new LimitSeconds("os-performance-limit-flags-set", flagged, flaggedBusy));
        return new LimitSummary("cpu", valid, busyCount * interval, reasons);
    }

    /// <summary>
    /// 30 秒の窓を 1 サンプルずつ動かし、温度が 3 ℃以上上がり・クロックが 5% 以上下がり・負荷が 50% 以上だった窓を印し、
    /// 重なる窓を 1 つの時間帯にまとめる。大きい順に最大 6 つ、時間順で返す。温度とクロックは 5 サンプルの移動平均で均す。
    /// </summary>
    internal static List<CoOccurrence> FindCoOccurrences(string device, double[] t, double[] temp, double[] clock, double[]? load)
    {
        var st = Stat.TrailingMean(temp, 5);
        var sc = Stat.TrailingMean(clock, 5);
        double[]? sl = load is null ? null : Stat.TrailingMean(load, 5);
        var flagged = new List<(int A, int B)>();
        int j = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (j < i) j = i;
            while (j < t.Length && t[j] < t[i] + CoWindowSec) j++;
            if (j >= t.Length) break;
            if (double.IsNaN(st[i]) || double.IsNaN(st[j]) || double.IsNaN(sc[i]) || double.IsNaN(sc[j])) continue;
            if (sc[i] < CoMinClockMHz) continue;
            double rise = st[j] - st[i];
            double drop = (sc[i] - sc[j]) / sc[i];
            if (rise < CoTempRiseC || drop < CoClockDropRatio) continue;
            if (sl is not null)
            {
                double mean = Stat.MeanRange(sl, i, j);
                if (!(mean >= BusyPct)) continue;
            }
            flagged.Add((i, j));
        }

        var merged = new List<(int A, int B)>();
        foreach (var (a, b) in flagged)
        {
            if (merged.Count > 0 && a <= merged[^1].B) merged[^1] = (merged[^1].A, Math.Max(merged[^1].B, b));
            else merged.Add((a, b));
        }

        var list = merged
            .Select(m => new CoOccurrence(device, t[m.A], t[m.B], st[m.A], st[m.B], sc[m.A], sc[m.B],
                                          load is null ? double.NaN : Stat.MeanRange(load, m.A, m.B)))
            .OrderByDescending(c => c.TempToC - c.TempFromC)
            .Take(MaxCoOccurrences)
            .OrderBy(c => c.StartSec)
            .ToList();
        return list;
    }

    private static List<SpanSummary> Spans(SeriesSet s, IReadOnlyList<RunMarker> markers, List<SummaryNote> notes)
    {
        var spans = new List<SpanSummary>();
        foreach (var (kind, startKind, endKind) in new[]
                 { ("bench", MarkerKind.BenchStart, MarkerKind.BenchEnd), ("stress", MarkerKind.StressStart, MarkerKind.StressEnd) })
        {
            var ordered = markers.Where(m => m.Kind == startKind || m.Kind == endKind).OrderBy(m => m.TSec).ToList();
            RunMarker? open = null;
            foreach (var m in ordered)
            {
                if (m.Kind == startKind)
                {
                    // 終わりの印が無いまま次の開始が来たら、そこで前の区間を閉じる
                    if (open is not null) spans.Add(MakeSpan(s, kind, open.TSec, m.TSec, isOpen: true));
                    open = m;
                }
                else if (open is not null)
                {
                    spans.Add(MakeSpan(s, kind, open.TSec, m.TSec, isOpen: false));
                    open = null;
                }
                else notes.Add(new SummaryNote("marker-unpaired", MarkerKinds.Code(m.Kind)));
            }
            if (open is not null) spans.Add(MakeSpan(s, kind, open.TSec, s.T[^1], isOpen: true));
        }
        return spans.OrderBy(x => x.StartSec).ToList();
    }

    private static SpanSummary MakeSpan(SeriesSet s, string kind, double from, double to, bool isOpen)
    {
        var picks = new List<int>();
        for (int i = 0; i < s.Count; i++) if (s.T[i] >= from && s.T[i] <= to) picks.Add(i);
        var list = new List<SpanMetric>();
        foreach (var (key, values) in s.Columns.OrderBy(c => ColumnOrder(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (IsSpecial(key) || !IsSpanMetric(key)) continue;
            var v = new List<double>();
            foreach (int i in picks) if (!double.IsNaN(values[i])) v.Add(values[i]);
            if (v.Count == 0) continue;
            list.Add(new SpanMetric(key, MetricKeys.UnitOf(key), v.Min(), v.Average(), v.Max(), Stat.Median(v)));
        }
        return new SpanSummary(kind, from, to, isOpen, list);
    }

    /// <summary>区間の要約に出す項目: CPU と GPU の使用率・クロック・温度・電力。</summary>
    private static bool IsSpanMetric(string key)
    {
        if (key is MetricKeys.CpuLoad or MetricKeys.CpuClock or MetricKeys.CpuPackagePower) return true;
        return MetricKeys.TryParseGpu(key, out _, out string m)
               && m is MetricKeys.Util or MetricKeys.Clock or MetricKeys.Temp or MetricKeys.Power;
    }
}

/// <summary>統計の小道具。NaN（読めなかった値）は数えない。</summary>
internal static class Stat
{
    public static bool TryStats(double[] v, out int count, out double min, out double avg, out double max)
    {
        count = 0; min = double.PositiveInfinity; max = double.NegativeInfinity; avg = 0;
        double sum = 0;
        foreach (double x in v)
        {
            if (double.IsNaN(x)) continue;
            count++; sum += x;
            if (x < min) min = x;
            if (x > max) max = x;
        }
        if (count == 0) { min = max = avg = double.NaN; return false; }
        avg = sum / count;
        return true;
    }

    public static double Median(List<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2;
    }

    /// <summary>直近 window 個（自分を含む）の平均。読めた値が 1 つも無ければ NaN。</summary>
    public static double[] TrailingMean(double[] v, int window)
    {
        var r = new double[v.Length];
        double sum = 0; int cnt = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if (!double.IsNaN(v[i])) { sum += v[i]; cnt++; }
            int out0 = i - window;
            if (out0 >= 0 && !double.IsNaN(v[out0])) { sum -= v[out0]; cnt--; }
            r[i] = cnt > 0 ? sum / cnt : double.NaN;
        }
        return r;
    }

    public static double MeanRange(double[] v, int a, int b)
    {
        double sum = 0; int cnt = 0;
        for (int i = a; i <= b; i++) if (!double.IsNaN(v[i])) { sum += v[i]; cnt++; }
        return cnt > 0 ? sum / cnt : double.NaN;
    }
}
