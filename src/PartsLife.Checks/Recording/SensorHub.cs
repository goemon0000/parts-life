namespace PartsLife.Checks.Recording;

/// <summary>
/// 読み口の束を動かす。**検出（開始のとき 1 回）・基準取り・毎秒の読み取り**を、読み口ごとの専用の糸でまとめて行う。
///
/// - 毎秒の読み取りは全部同時に始め、決めた時間（既定 0.8 秒）で見切る。戻らなかった読み口の値は NaN、回数は数える
/// - 検出に戻らなかった・例外を投げた読み口は、その印（`source.&lt;id&gt;`）を 1 行「読めない」で残す。他の読み口は続く
/// - 同じ項目を 2 つの読み口が名乗ったら、先の方だけ使う
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly List<SourceRunner> _runners = new();
    private bool _disposed;

    public CapabilityReport Report { get; private set; } = new(Array.Empty<MetricStatus>(), Array.Empty<GpuDescriptor>(), 0);

    /// <summary>記録に出る列（読めた項目だけ。読み口の並びの順）。</summary>
    public IReadOnlyList<string> Columns { get; private set; } = Array.Empty<string>();

    /// <summary>毎秒の読み取りを待つ上限。1 秒の間隔より短くする（戻らない読み口が、次の秒を食わないように）。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>読み口の名前 → 見切った回数（戻らなかった・前のが戻っていなかった）。</summary>
    public IReadOnlyDictionary<string, int> TimeoutCounts =>
        _runners.Where(r => r.Timeouts + r.Skipped > 0).ToDictionary(r => r.Source.Id, r => r.Timeouts + r.Skipped);

    private SensorHub() { }

    /// <summary>読み口を開いて検出する。<paramref name="detectTimeout"/> は読み口 1 つあたりの上限。</summary>
    public static SensorHub Open(SourceSet set, TimeSpan? detectTimeout = null, TimeSpan? readTimeout = null)
    {
        var hub = new SensorHub { ReadTimeout = readTimeout ?? TimeSpan.FromMilliseconds(800) };
        var timeout = detectTimeout ?? TimeSpan.FromSeconds(6);
        var statuses = new List<MetricStatus>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // 全部の読み口に同時に検出を頼む（NVML の初期化などは数百ミリ秒かかることがある）
            var jobs = new List<(SourceRunner Runner, long Ticket, IReadOnlyList<MetricStatus>[] Slot)>();
            foreach (var source in set.Sources)
            {
                var runner = new SourceRunner(source);
                hub._runners.Add(runner);
                var slot = new IReadOnlyList<MetricStatus>[1];
                if (runner.TryPost(() => slot[0] = source.Detect(), out long ticket)) jobs.Add((runner, ticket, slot));
            }
            foreach (var (runner, ticket, slot) in jobs)
            {
                var outcome = runner.Wait(ticket, timeout);
                var id = runner.Source.Id;
                if (outcome == SourceRunner.Outcome.Done && slot[0] is { } list)
                {
                    var own = new List<MetricStatus>();
                    foreach (var m in list)
                        if (seen.Add(m.Key)) own.Add(m);
                    statuses.AddRange(own);
                    runner.Buffer = new SampleRow(own.Where(m => m.IsAvailable).Select(m => m.Key));
                }
                else
                {
                    string why = outcome == SourceRunner.Outcome.Timeout ? Reasons.Timeout : Reasons.Failed;
                    statuses.Add(new MetricStatus("source." + id, "", id, Availability.Unavailable, why));
                }
            }
        }
        catch
        {
            hub.Dispose();
            throw;
        }

        hub.Columns = statuses.Where(m => m.IsAvailable).Select(m => m.Key).ToArray();
        hub.Report = new CapabilityReport(statuses, set.Gpus, set.LogicalProcessors);
        return hub;
    }

    /// <summary>記録の直前に 1 回。差分で値を出す読み口の基準を取る。</summary>
    public void Prime()
    {
        var jobs = new List<(SourceRunner, long)>();
        foreach (var r in _runners)
        {
            if (r.Buffer is null || r.Buffer.Count == 0) continue;
            var src = r.Source;
            if (r.TryPost(src.Prime, out long t)) jobs.Add((r, t));
        }
        foreach (var (r, t) in jobs) r.Wait(t, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 全部の読み口から 1 秒ぶんを読んで row に入れる。戻らなかった読み口の値は NaN のまま。
    /// 戻り値は、今回戻らなかった（見切った・飛ばした）読み口の数。
    /// </summary>
    public int ReadInto(SampleRow row)
    {
        row.Clear();
        var jobs = new List<(SourceRunner Runner, long Ticket)>();
        int failed = 0;
        foreach (var r in _runners)
        {
            if (r.Buffer is not { Count: > 0 } buffer) continue;
            var src = r.Source;
            if (r.TryPost(() => { buffer.Clear(); src.Read(buffer); }, out long t)) jobs.Add((r, t));
            else failed++;
        }
        var deadline = DateTime.UtcNow + ReadTimeout;
        foreach (var (r, t) in jobs)
        {
            var left = deadline - DateTime.UtcNow;
            var outcome = r.Wait(t, left > TimeSpan.Zero ? left : TimeSpan.Zero);
            if (outcome == SourceRunner.Outcome.Done) row.CopyFrom(r.Buffer!);
            else failed++;
        }
        return failed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var r in _runners) r.Dispose();
    }
}
