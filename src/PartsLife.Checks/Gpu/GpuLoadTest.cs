using System.Diagnostics;
using PartsLife.Checks.Load;

namespace PartsLife.Checks.Gpu;

public sealed class GpuLoadOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>DXGI のアダプタ番号。null なら WARP（CPU で動く。テスト専用）。</summary>
    public int? AdapterIndex { get; init; }
    public string AdapterName { get; init; } = "";
    /// <summary>
    /// 温度の読み口。**null なら試験をしない**（見張れない GPU には負荷をかけない）。
    /// </summary>
    public Func<int?>? Temperature { get; init; }
    /// <summary>この温度に達したら即座に止める。</summary>
    public int AbortCelsius { get; init; } = 83;
    /// <summary>
    /// 温度の読み取り1回に待つ時間。**これを超えたら「読めなかった」に数える**
    /// （固まった読み取りを待ち続けると、負荷を止める者がいなくなる）。
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>
    /// 働かせる割合。**控えめに 75%。** 1回 30 ミリ秒ほど回して、10 ミリ秒休む。
    /// 修理明けのカードもあるので、全開で焼くような試験にはしない。
    /// </summary>
    public double Duty { get; init; } = 0.75;
    public int Groups { get; init; } = 4096;
    public TimeSpan TargetBatch { get; init; } = TimeSpan.FromMilliseconds(30);
}

public sealed record GpuTick(int Second, double GigaOpsPerSec, int? TemperatureC, long Mismatches);

public sealed class GpuLoadResult
{
    public string AdapterName { get; set; } = "";
    public List<GpuTick> Ticks { get; } = new();
    public double Seconds { get; set; }
    public bool Ran { get; set; }
    public bool Stopped { get; set; }
    public string? AbortReason { get; set; }
    public bool AbortedForHeat { get; set; }
    /// <summary>
    /// 温度の読み取りが戻ってこないまま終わった。読み口（nvml.dll）の中にまだ糸がいるかもしれないので、
    /// **呼んだ側はその DLL を解放してはいけない**（<see cref="Nvml.Dispose"/> は自分で確かめる）。
    /// </summary>
    public bool TemperatureReaderStuck { get; set; }
    public int? MaxTemperature { get; set; }
    public int? StartTemperature { get; set; }
    public long Mismatches { get; set; }
    public long Checks { get; set; }
    public long Batches { get; set; }
    public uint Iterations { get; set; }
    public SteadinessResult? Steadiness { get; set; }
}

/// <summary>
/// GPU の負荷テスト。D3D11 の計算シェーダを回し、**温度を見張りながら**控えめに負荷をかける。
///
/// ■ 安全側に倒す
/// - 温度が読めない GPU には負荷をかけない（NVIDIA 以外は今のところ全部これ。TODO: AMD は ADLX）
/// - 始める前に既に 78 ℃ 以上なら始めない
/// - 0.5 秒ごとに温度を読み、83 ℃ に達したら即座に止める。3 回続けて読めなくても止める
///   （1.5 秒以内に戻らない読み取りも「読めなかった」に数える。固まった読み取りを待ち続けない）
/// - 1 回の計算は 30 ミリ秒ほど。Windows が「GPU が固まった」と判断する 2 秒には遠く及ばない
/// - 働かせるのは 75% の時間だけ
/// ■ 答え合わせをする
/// 整数だけの計算なので CPU で同じ答えを作れる。2 秒ごとに一部を読み戻して照合する。
/// </summary>
public static class GpuLoadTest
{
    public static Task<GpuLoadResult> RunAsync(GpuLoadOptions o, IProgress<GpuTick>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(o, progress, ct), CancellationToken.None,
                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static GpuLoadResult Run(GpuLoadOptions o, IProgress<GpuTick>? progress, CancellationToken ct)
    {
        var r = new GpuLoadResult { AdapterName = o.AdapterName };
        if (o.Temperature is null)
        {
            r.AbortReason = L.T("この GPU は温度を見張れないので、負荷はかけませんでした",
                                "this GPU's temperature cannot be watched, so no load was applied");
            return r;
        }
        // 読み取りは専用の糸で。戻らなければ見切る（Dispose は待たない。固まった糸は放っておく）
        using var probe = new TimedReader(o.Temperature, "PartsLife.GpuTempRead");
        bool t0Read = probe.TryRead(o.ReadTimeout, out int? t0);
        r.StartTemperature = r.MaxTemperature = t0;
        if (!t0Read)
        {
            r.TemperatureReaderStuck = probe.Busy;
            r.AbortReason = L.T("温度の読み取りが応答しなかったので始めませんでした",
                                "the temperature reading did not respond, so did not start");
            return r;
        }
        if (t0 is null)
        {
            r.AbortReason = L.T("温度が読めなかったので始めませんでした", "could not read the temperature, so did not start");
            return r;
        }
        if (t0 >= o.AbortCelsius - 5)
        {
            r.AbortReason = L.T($"始める前から {t0} ℃ あるので始めませんでした", $"already at {t0} °C before starting, so did not start");
            r.AbortedForHeat = true;
            return r;
        }

        // 温度の見張り（別スレッド）
        int latestTemp = t0.Value, misses = 0;
        string? watchAbort = null;
        bool heat = false, hung = false;
        using var stopWatch = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            while (!stopWatch.Token.WaitHandle.WaitOne(500))
            {
                bool answered = probe.TryRead(o.ReadTimeout, out int? t);
                if (!answered || t is null)
                {
                    hung |= !answered;
                    if (++misses >= 3)
                    {
                        watchAbort = hung
                            ? L.T("温度の読み取りが応答しなくなったので止めました", "stopped: the temperature reading stopped responding")
                            : L.T("温度が読めなくなったので止めました", "stopped: temperature could not be read");
                        return;
                    }
                    continue;
                }
                misses = 0;
                Volatile.Write(ref latestTemp, t.Value);
                if (t >= o.AbortCelsius)
                {
                    heat = true;
                    watchAbort = L.T($"{t} ℃ に達したので止めました（上限 {o.AbortCelsius} ℃）。冷却を確かめてください",
                                     $"stopped at {t} °C (limit {o.AbortCelsius} °C). Check the cooling");
                    return;
                }
            }
        }) { IsBackground = true, Name = "PartsLife.GpuTemp" };
        watcher.Start();

        var sw = Stopwatch.StartNew();
        try
        {
            using var gpu = new D3D11Compute(o.AdapterIndex, o.Groups);
            r.Ran = true;
            uint iters = Calibrate(gpu, o.TargetBatch, () => ct.IsCancellationRequested || watchAbort is not null);
            r.Iterations = iters;

            uint seed = 1;
            double ops = 0;
            var tickStart = sw.Elapsed;
            var lastCheck = sw.Elapsed;
            int second = 0;
            while (sw.Elapsed < o.Duration)
            {
                if (ct.IsCancellationRequested) { r.Stopped = true; break; }
                if (watchAbort is not null) break;

                var took = gpu.Dispatch(seed, iters, TimeSpan.FromMilliseconds(1500));
                r.Batches++;
                ops += (double)gpu.Elements * iters;

                if (sw.Elapsed - lastCheck >= TimeSpan.FromSeconds(2))
                {
                    var got = gpu.ReadBack(256);
                    for (uint i = 0; i < got.Length; i++)
                        if (got[i] != D3D11Compute.Reference(i, seed, iters)) r.Mismatches++;
                    r.Checks += got.Length;
                    lastCheck = sw.Elapsed;
                }
                seed++;

                // 休む（働かせる割合を守る）
                double rest = took.TotalMilliseconds * (1 - o.Duty) / Math.Max(0.05, o.Duty);
                if (rest > 0 && ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(rest))) { r.Stopped = true; break; }

                if (sw.Elapsed - tickStart >= TimeSpan.FromSeconds(1))
                {
                    double sec = (sw.Elapsed - tickStart).TotalSeconds;
                    second++;
                    var tick = new GpuTick(second, ops / sec / 1e9, Volatile.Read(ref latestTemp), r.Mismatches);
                    r.Ticks.Add(tick);
                    progress?.Report(tick);
                    ops = 0;
                    tickStart = sw.Elapsed;
                }
            }
        }
        catch (TimeoutException)
        {
            r.AbortReason = L.T("GPU の応答が遅れたので止めました", "stopped: the GPU did not respond in time");
        }
        catch (InvalidOperationException ex)
        {
            r.AbortReason = L.T("GPU の計算を始められませんでした: ", "could not start GPU compute: ") + ex.Message;
        }
        catch (DllNotFoundException ex)
        {
            r.AbortReason = L.T("D3D11 が使えませんでした: ", "D3D11 is not available: ") + ex.Message;
        }
        finally
        {
            stopWatch.Cancel();
            // 見張りの糸は読み取りを最大 ReadTimeout しか待たないので、ここで必ず戻る
            if (!watcher.Join(o.ReadTimeout + TimeSpan.FromSeconds(1)))
                System.Diagnostics.Trace.TraceWarning("PartsLife: the GPU temperature watcher did not exit in time.");
            r.TemperatureReaderStuck = probe.Busy;
        }

        if (watchAbort is not null) { r.AbortReason = watchAbort; r.AbortedForHeat = heat; }
        r.Seconds = sw.Elapsed.TotalSeconds;
        var temps = r.Ticks.Where(t => t.TemperatureC is not null).Select(t => t.TemperatureC!.Value).ToList();
        int last = Volatile.Read(ref latestTemp);
        r.MaxTemperature = new[] { r.StartTemperature ?? 0, last }.Concat(temps).Max();
        r.Steadiness = Steadiness.Compute(r.Ticks.Select(t => t.GigaOpsPerSec).ToList());
        return r;
    }

    /// <summary>1回がおよそ <paramref name="target"/> になる反復回数を探す。</summary>
    private static uint Calibrate(D3D11Compute gpu, TimeSpan target, Func<bool> stop)
    {
        uint iters = 64;
        double ms = gpu.Dispatch(1, iters, TimeSpan.FromMilliseconds(1500)).TotalMilliseconds;
        while (ms < 5 && iters < (1u << 20) && !stop())
        {
            iters *= 2;
            ms = gpu.Dispatch(1, iters, TimeSpan.FromMilliseconds(1500)).TotalMilliseconds;
        }
        if (ms <= 0) return iters;
        double scaled = iters * target.TotalMilliseconds / ms;
        return (uint)Math.Clamp(scaled, 16, 1 << 22);
    }

    public static IReadOnlyList<Finding> Judge(GpuLoadResult r)
    {
        var f = new List<Finding>();
        if (!r.Ran)
        {
            f.Add(new Finding("GPU", r.AbortedForHeat ? Verdict.Caution : Verdict.Unavailable,
                r.AbortReason ?? L.T("行いませんでした", "did not run")));
            return f;
        }
        if (r.AbortReason is { } why)
            f.Add(new Finding(L.T("中断", "Stopped"), r.AbortedForHeat ? Verdict.Check : Verdict.Caution, why));

        f.Add(r.Mismatches > 0
            ? new Finding(L.T("計算の答え合わせ", "Computation check"), Verdict.Check,
                L.T($"{L.N(r.Mismatches)} か所で答えが違いました。GPU が計算を間違えています（クロックの上げすぎ・熱・故障を疑ってください）",
                    $"{L.N(r.Mismatches)} wrong results. The GPU miscalculated (suspect overclock, heat or a fault)"))
            : r.Checks > 0
                ? new Finding(L.T("計算の答え合わせ", "Computation check"), Verdict.Ok, L.T($"{L.N(r.Checks)} 個すべて一致", $"all {L.N(r.Checks)} values matched"))
                : new Finding(L.T("計算の答え合わせ", "Computation check"), Verdict.Unavailable, L.T("照合する前に終わりました", "ended before the first check")));

        if (r.MaxTemperature is { } mt)
            f.Add(new Finding(L.T("温度", "Temperature"), mt >= 80 ? Verdict.Caution : Verdict.Ok,
                L.T($"開始 {r.StartTemperature} ℃ → 最高 {mt} ℃（{(int)(r.Seconds)} 秒、働かせた割合 75%）",
                    $"start {r.StartTemperature} °C → peak {mt} °C ({(int)r.Seconds} s at 75% duty)")));

        if (r.Steadiness is { } s)
        {
            var v = Steadiness.Judge(s);
            f.Add(new Finding(L.T("持続性", "Steadiness"), v,
                L.T($"終盤は序盤の {s.Score:0}%（ばらつき {s.Cv * 100:0.0}%）", $"ended at {s.Score:0}% of the opening pace (variation {s.Cv * 100:0.0}%)") +
                (v == Verdict.Ok ? "" : L.T("。熱か電力の制限で絞られた可能性", ". Possibly throttled by heat or power limits"))));
        }
        else
            f.Add(new Finding(L.T("持続性", "Steadiness"), Verdict.Unavailable, L.T("短すぎて判定できません", "too short to judge")));

        if (r.Ticks.Count > 0)
            f.Add(new Finding(L.T("この試験での速さ", "Throughput in this test"), Verdict.Ok,
                L.T($"平均 {r.Ticks.Average(t => t.GigaOpsPerSec):0.0} G回/秒（この試験独自の単位。他の測定値とは比べられません）",
                    $"{r.Ticks.Average(t => t.GigaOpsPerSec):0.0} G iterations/s on average (this test's own unit)")));
        return f;
    }
}
