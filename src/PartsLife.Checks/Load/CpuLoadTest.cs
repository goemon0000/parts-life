using System.Diagnostics;

namespace PartsLife.Checks.Load;

public sealed class CpuLoadOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>使う論理プロセッサの数。null なら全部。（テストで小さくするため）</summary>
    public int? Threads { get; init; }
    /// <summary>スレッドを論理プロセッサに縛る。縛らないとコア別の値が混ざる。</summary>
    public bool Pin { get; init; } = true;
}

/// <summary>1秒ぶんの様子。</summary>
public sealed record CpuTick(int Second, double[] PerCpu, double Total, ClockSample Clock, long ComputeErrors);

public sealed class CpuLoadResult
{
    public IReadOnlyList<LogicalCpu> Cpus { get; init; } = Array.Empty<LogicalCpu>();
    public List<CpuTick> Ticks { get; } = new();
    public double Seconds { get; set; }
    public bool Stopped { get; set; }
    public long ComputeErrors { get; set; }
    public long Chunks { get; set; }
    /// <summary>論理プロセッサごとの平均（1秒あたりの仕事量。このテスト独自の単位）。</summary>
    public double[] PerCpuAverage { get; set; } = Array.Empty<double>();
    public SteadinessResult? Steadiness { get; set; }
    public SteadinessResult? ClockSteadiness { get; set; }
}

/// <summary>
/// CPU の負荷テスト。全論理プロセッサに同じ計算を回し続ける。
///
/// ■ 止めるとすぐ止まる
/// 計算は 1 回あたり 1 ミリ秒未満の小分けにしてあり、毎回「止め」の旗を見る。
/// ■ 計算の答え合わせをする
/// 同じ入力からは必ず同じ答えが出る計算にしてあり、答えを事前に1回だけ出しておく。
/// **負荷をかけた状態で答えが違ったら、それは CPU が計算を間違えている**
/// （クロックの上げすぎ・電圧不足・熱）。この試験で一番重い所見。
/// ■ 優先度は「通常以下」
/// 全コアを使っても、マウスや窓の操作は先に通る。
/// </summary>
public static class CpuLoadTest
{
    public const int ChunkIterations = 1 << 16;
    private const int Seeds = 16;
    private const int Stride = 16;     // カウンタを 128 バイトずつ離す（隣のコアと取り合わない）

    /// <summary>
    /// 計算の小分け1回。**同じ seed からは必ず同じ答え。**
    /// 整数の掛け算・シフトと浮動小数点の掛け算・足し算を混ぜる。
    /// </summary>
    public static ulong Chunk(ulong seed, int iterations = ChunkIterations)
    {
        ulong x = seed | 1;
        double d = (seed & 0xFFFF) * 1e-3 + 1.0;
        for (int i = 0; i < iterations; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            x = unchecked(x * 0x9E3779B97F4A7C15UL + (ulong)i);
            d = d * 1.0000001 + 0.5;
            if (d > 1e6) d -= 999_999.0;
        }
        return x ^ BitConverter.DoubleToUInt64Bits(d);
    }

    public static Task<CpuLoadResult> RunAsync(CpuLoadOptions options, IProgress<CpuTick>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(options, progress, ct), CancellationToken.None,
                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static CpuLoadResult Run(CpuLoadOptions options, IProgress<CpuTick>? progress, CancellationToken ct)
    {
        var all = CpuTopology.Enumerate();
        int n = Math.Clamp(options.Threads ?? all.Count, 1, all.Count);
        var cpus = all.Take(n).ToList();
        var result = new CpuLoadResult { Cpus = cpus };

        // 答えを先に出しておく（負荷をかける前、1スレッドで）
        var expected = new ulong[Seeds];
        for (int k = 0; k < Seeds; k++) expected[k] = Chunk((ulong)k * 0x1234567UL + 99);

        var counters = new long[n * Stride];
        long errors = 0;
        int stop = 0;

        var threads = new Thread[n];
        for (int t = 0; t < n; t++)
        {
            int me = t;
            threads[t] = new Thread(() =>
            {
                if (options.Pin) CpuTopology.PinCurrentThread(cpus[me]);
                int k = me % Seeds;
                long done = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    ulong got = Chunk((ulong)k * 0x1234567UL + 99);
                    if (got != expected[k]) Interlocked.Increment(ref errors);
                    k = (k + 1) % Seeds;
                    Volatile.Write(ref counters[me * Stride], ++done);
                }
            })
            {
                IsBackground = true,        // 本体が終わるときに道連れにする（残って回り続けない）
                Priority = ThreadPriority.BelowNormal,
                Name = $"PartsLife.CpuLoad.{t}",
            };
        }

        using var clock = new ClockSampler();
        var sw = Stopwatch.StartNew();
        foreach (var th in threads) th.Start();

        var prev = new long[n];
        var sums = new double[n];
        int seconds = 0;
        var nextTick = TimeSpan.FromSeconds(1);
        var lastSample = TimeSpan.Zero;
        try
        {
            while (sw.Elapsed < options.Duration)
            {
                var wait = nextTick - sw.Elapsed;
                if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait)) { result.Stopped = true; break; }
                if (ct.IsCancellationRequested) { result.Stopped = true; break; }

                var now0 = sw.Elapsed;
                double span = Math.Max(0.001, (now0 - lastSample).TotalSeconds);
                lastSample = now0;
                nextTick += TimeSpan.FromSeconds(1);
                var per = new double[n];
                for (int i = 0; i < n; i++)
                {
                    long now = Volatile.Read(ref counters[i * Stride]);
                    per[i] = (now - prev[i]) * (double)ChunkIterations / span / 1e6;   // 百万回/秒
                    prev[i] = now;
                }
                seconds++;
                var tick = new CpuTick(seconds, per, per.Sum(), clock.Sample(), Interlocked.Read(ref errors));
                result.Ticks.Add(tick);
                progress?.Report(tick);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var th in threads) th.Join();
        }

        result.Seconds = sw.Elapsed.TotalSeconds;
        result.ComputeErrors = Interlocked.Read(ref errors);
        for (int i = 0; i < n; i++) result.Chunks += Volatile.Read(ref counters[i * Stride]);

        // コア別の平均は立ち上がりの2秒を除いて出す
        var steady = result.Ticks.Skip(result.Ticks.Count > 4 ? 2 : 0).ToList();
        result.PerCpuAverage = new double[n];
        if (steady.Count > 0)
            for (int i = 0; i < n; i++) result.PerCpuAverage[i] = steady.Average(t => t.PerCpu[i]);
        result.Steadiness = Steadiness.Compute(result.Ticks.Select(t => t.Total).ToList());
        var clocks = result.Ticks.Where(t => t.Clock.EffectiveMHz is not null).Select(t => t.Clock.EffectiveMHz!.Value).ToList();
        if (clocks.Count == result.Ticks.Count) result.ClockSteadiness = Steadiness.Compute(clocks);
        return result;
    }

    /// <summary>結果の判定。**純粋な関数。**</summary>
    public static IReadOnlyList<Finding> Judge(CpuLoadResult r)
    {
        var f = new List<Finding>();

        if (r.ComputeErrors > 0)
            f.Add(new Finding(L.T("計算の答え合わせ", "Computation check"), Verdict.Check,
                L.T($"{L.N(r.ComputeErrors)} 回、答えが合いませんでした。CPU が計算を間違えています（クロックの上げすぎ・電圧不足・熱を疑ってください）",
                    $"{L.N(r.ComputeErrors)} wrong results. The CPU miscalculated under load (suspect overclock, undervolt or heat)")));
        else
            f.Add(new Finding(L.T("計算の答え合わせ", "Computation check"), Verdict.Ok,
                L.T($"{L.N(r.Chunks)} 回すべて一致", $"all {L.N(r.Chunks)} results matched")));

        if (r.Steadiness is { } s)
        {
            var v = Steadiness.Judge(s);
            string why = v == Verdict.Ok
                ? L.T($"終盤も序盤の {s.Score:0}% を保っています（ばらつき {s.Cv * 100:0.0}%）",
                      $"held {s.Score:0}% of the opening pace (variation {s.Cv * 100:0.0}%)")
                : L.T($"終盤は序盤の {s.Score:0}%（ばらつき {s.Cv * 100:0.0}%）。熱か電力の制限で絞られた可能性（ブーストの持続時間切れでも起きます）",
                      $"ended at {s.Score:0}% of the opening pace (variation {s.Cv * 100:0.0}%). Possibly throttled by heat or power limits (boost time running out does this too)");
            f.Add(new Finding(L.T("持続性", "Steadiness"), v, why));
        }
        else
            f.Add(new Finding(L.T("持続性", "Steadiness"), Verdict.Unavailable,
                L.T("短すぎて判定できません（8 秒以上必要）", "too short to judge (needs 8 s or more)")));

        // コア別: 同じ種類のコアどうしで比べる
        if (r.PerCpuAverage.Length > 1 && r.PerCpuAverage.Any(x => x > 0))
        {
            var slow = new List<string>();
            foreach (var cls in r.Cpus.GroupBy(c => c.EfficiencyClass))
            {
                var vals = cls.Select(c => (c, v: r.PerCpuAverage[c.Index])).ToList();
                double median = vals.Select(x => x.v).OrderBy(x => x).ElementAt(vals.Count / 2);
                foreach (var (c, v) in vals)
                    if (median > 0 && v < median * 0.75) slow.Add($"#{c.Index}({v / median * 100:0}%)");
            }
            int classes = r.Cpus.Select(c => c.EfficiencyClass).Distinct().Count();
            string kinds = classes > 1 ? L.T($"（コアの種類 {classes} 種、種類ごとに比較）", $" ({classes} core types, compared within type)") : "";
            f.Add(slow.Count == 0
                ? new Finding(L.T("コアごとの速さ", "Per-core pace"), Verdict.Ok,
                    L.T($"{r.PerCpuAverage.Length} 論理プロセッサとも揃っています", $"all {r.PerCpuAverage.Length} logical processors are even") + kinds)
                : new Finding(L.T("コアごとの速さ", "Per-core pace"), Verdict.Caution,
                    L.T($"同じ種類の中央値より遅い: {string.Join(" ", slow.Take(8))}。ほかの作業が載っていた可能性もあります",
                        $"slower than the median of their type: {string.Join(" ", slow.Take(8))}. Other work may have been running") + kinds));
        }

        var clocks = r.Ticks.Select(t => t.Clock.EffectiveMHz).Where(x => x is not null).Select(x => x!.Value).ToList();
        if (clocks.Count > 0)
        {
            double baseMhz = r.Ticks.Select(t => t.Clock.BaseMHz).FirstOrDefault(x => x is not null) ?? 0;
            string text = L.T($"平均 {clocks.Average() / 1000:0.00} GHz（最初 {clocks[0] / 1000:0.00} → 最後 {clocks[^1] / 1000:0.00}）",
                              $"average {clocks.Average() / 1000:0.00} GHz (first {clocks[0] / 1000:0.00} → last {clocks[^1] / 1000:0.00})") +
                          (baseMhz > 0 ? L.T($"、基準 {baseMhz / 1000:0.00} GHz", $", base {baseMhz / 1000:0.00} GHz") : "");
            var v = r.ClockSteadiness is { } cs && cs.Score < 85 ? Verdict.Caution : Verdict.Ok;
            if (v == Verdict.Caution) text += L.T("。後半でクロックが下がっています", ". The clock dropped in the second half");
            f.Add(new Finding(L.T("実効クロック", "Effective clock"), v, text));
        }
        else
            f.Add(new Finding(L.T("実効クロック", "Effective clock"), Verdict.Unavailable,
                L.T("この環境では Windows がクロックを公開していません", "Windows does not expose the clock on this system")));

        var loads = r.Ticks.Select(t => t.Clock.LoadPercent).Where(x => x is not null).Select(x => x!.Value).ToList();
        if (loads.Count > 0)
            f.Add(new Finding(L.T("CPU 使用率", "CPU load"), Verdict.Ok,
                L.T($"試験中の平均 {loads.Average():0}%", $"{loads.Average():0}% on average during the test")));

        f.Add(new Finding(L.T("温度", "Temperature"), Verdict.Unavailable,
            L.T("CPU の温度はカーネルドライバ無しには読めないので測っていません", "CPU temperature needs a kernel driver, so it is not measured")));
        return f;
    }
}
