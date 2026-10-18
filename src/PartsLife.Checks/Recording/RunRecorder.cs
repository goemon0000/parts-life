using System.Diagnostics;
using System.Text;

namespace PartsLife.Checks.Recording;

/// <summary>記録を始められなかった理由。<see cref="Code"/>: "folder"（作れない・書けない）/ "low-disk"（空きが足りない）/ "state"（使い方の誤り）。</summary>
public sealed class RecordException : Exception
{
    public string Code { get; }
    public RecordException(string code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
}

public sealed class RunOptions
{
    /// <summary>記録の置き場。既定は ドキュメント\PartsLife\runs（利用者が見える所。リダイレクトされたドキュメントにも従う）。</summary>
    public string Folder { get; init; } = RunStore.DefaultFolder();
    public string AppVersion { get; init; } = "";
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>これを超えたら自動で止める（ファイルが際限なく大きくならないように）。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(12);
    public long MinFreeBytes { get; init; } = 200L * 1024 * 1024;
    public TimeSpan DetectTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(800);
    public Func<SourceSet> Sources { get; init; } = DefaultSources.Create;
    public IRunClock Clock { get; init; } = SystemRunClock.Instance;
    public Func<string> NewRunId { get; init; } = RunId.New;
    public Func<int, SystemSnapshot> System { get; init; } = RunSystem.Snapshot;
}

/// <summary>毎秒の「いまの値」。画面の表示用。<see cref="Values"/> は読めた項目だけ。</summary>
public sealed class LiveSample
{
    public double TSec { get; }
    public IReadOnlyDictionary<string, double> Values { get; }
    public int FailedSources { get; }
    public LiveSample(double tSec, IReadOnlyDictionary<string, double> values, int failed) { TSec = tSec; Values = values; FailedSources = failed; }
    public double Get(string key) => Values.TryGetValue(key, out double v) ? v : double.NaN;
}

public sealed class RunResult
{
    public required RunSession Session { get; init; }
    public RunSummary? Summary { get; init; }
    public required string JsonPath { get; init; }
    public required string CsvPath { get; init; }
    /// <summary>要約を作れなかったとき（CSV が読めない等）の短い説明。</summary>
    public string? SummaryError { get; init; }
}

/// <summary>機械の素性（OS と電源プランとメモリ）。PC の名前・利用者名は読まない。</summary>
internal static class RunSystem
{
    public static SystemSnapshot Snapshot(int logicalProcessors)
    {
        string os = "Windows";
        int build = Environment.OSVersion.Version.Build;
        try
        {
            var o = SystemInfo.SystemCheck.ReadOs();
            os = Build.BuildRules.CleanName(o.Name, 60);
            build = o.Build;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new SystemSnapshot(os, build, PowerPlan.Active(), MemorySource.TotalMb(), logicalProcessors);
    }

}

/// <summary>
/// テストの記録（1 回ぶん）。**押したときだけ動く。** 使い方:
/// <code>
/// using var r = RunRecorder.Prepare(options);   // センサーを開いて、読めるかを判定する（r.Capabilities）
/// r.Start(buildSnapshot);                        // run-&lt;ID&gt;.json と sensors-&lt;ID&gt;.csv を作り、毎秒の記録を始める
/// r.AddMarker(MarkerKind.BenchStart);            // 任意の目印
/// var result = await r.StopAsync();              // 止めて、要約を作って、ファイルを閉じる
/// </code>
///
/// - 毎秒の記録は専用の糸 1 本。時計は単調（<c>t_s</c>）と UTC を両方書く。遅れたら取りこぼした分は飛ばす（記録の穴として残る）
/// - メモリは増えない。1 行ずつファイルへ書いて OS に渡す。要約は止めたあとに CSV を読み直して作る（読み直せるので、途中で落ちた記録も後から要約できる）
/// - 書き込みに失敗したら、そこで止めて「interrupted」にする。落ちない
/// - **通信しない。** 置き場はドキュメントの下だけ
/// </summary>
public sealed class RunRecorder : IDisposable
{
    private readonly RunOptions _o;
    private readonly SensorHub _hub;
    private readonly SourceSet _set;
    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<RunResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RunSession? _session;
    private CsvStream.Writer? _csv;
    private string _jsonPath = "", _csvPath = "";
    private double _t0;
    private Thread? _thread;
    private string _stopReason = "";
    private bool _started, _disposed;
    private readonly SampleRow _row;

    public CapabilityReport Capabilities => _hub.Report;
    public bool IsRecording => _started && !_done.Task.IsCompleted;
    public string? Id => _session?.RunId;
    public Task<RunResult> Completion => _done.Task;

    /// <summary>毎秒。記録の糸から呼ばれる（画面の糸ではない）。例外は握りつぶす。</summary>
    public event Action<LiveSample>? Sampled;
    /// <summary>止まったとき（利用者が止めた・時間の上限・書き込みの失敗）。理由は "user" / "max-duration" / "io-error" / "disposed"。</summary>
    public event Action<string>? Ended;

    private RunRecorder(RunOptions o, SourceSet set, SensorHub hub)
    {
        _o = o; _set = set; _hub = hub;
        _row = new SampleRow(hub.Columns);
    }

    /// <summary>センサーを開いて、項目ごとに読めるかを判定する（数秒かかることがある。画面の糸では呼ばないこと）。</summary>
    public static RunRecorder Prepare(RunOptions options)
    {
        var set = options.Sources();
        SensorHub hub;
        try { hub = SensorHub.Open(set, options.DetectTimeout, options.ReadTimeout); }
        catch
        {
            foreach (var s in set.Sources) { try { s.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
            throw;
        }
        return new RunRecorder(options, set, hub);
    }

    /// <summary>記録は始めずに、読めるかだけを調べて閉じる（「読めるセンサーを確認」）。</summary>
    public static CapabilityReport Probe(RunOptions options)
    {
        using var r = Prepare(options);
        return r.Capabilities;
    }

    public void Start(BuildSnapshot? build)
    {
        lock (_gate)
        {
            if (_started || _disposed) throw new RecordException("state", "already started or disposed");
            EnsureFolder(_o.Folder);
            long free = RunStore.FreeBytes(_o.Folder);
            if (free >= 0 && free < _o.MinFreeBytes) throw new RecordException("low-disk", $"free space {free} bytes");

            _hub.Prime();

            string id = _o.NewRunId();
            if (!RunId.IsValid(id)) throw new RecordException("state", "invalid run id");
            _jsonPath = RunStore.JsonPath(_o.Folder, id);
            _csvPath = RunStore.CsvPath(_o.Folder, id);
            if (File.Exists(_jsonPath) || File.Exists(_csvPath)) throw new RecordException("state", "run id collision");

            var clock = _o.Clock;
            _session = new RunSession
            {
                AppVersion = _o.AppVersion,
                RunId = id,
                Status = "recording",
                IntervalMs = (int)_o.Interval.TotalMilliseconds,
                System = _o.System(_set.LogicalProcessors),
                Build = build,
                Gpus = _set.Gpus.ToList(),
                Capabilities = _hub.Report.Metrics.ToList(),
                CsvFile = Path.GetFileName(_csvPath),
                Columns = new[] { CsvStream.TimeColumn, CsvStream.UtcColumn }.Concat(_hub.Columns).ToList(),
            };
            try
            {
                _csv = new CsvStream.Writer(_csvPath, _hub.Columns);
                _t0 = clock.MonotonicSeconds;
                _session.StartUtc = clock.UtcNow;
                WriteSession();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _csv?.Dispose();
                _csv = null;
                throw new RecordException("folder", ex.Message, ex);
            }

            _started = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PartsLife.Record.Loop" };
            _thread.Start();
        }
    }

    /// <summary>目印を足す（記録中だけ）。時刻は単調な時計で、記録の開始からの秒。ファイルにもすぐ残す。</summary>
    public RunMarker AddMarker(MarkerKind kind)
    {
        lock (_gate)
        {
            if (!IsRecording || _session is null) throw new RecordException("state", "not recording");
            var m = new RunMarker(kind, _o.Clock.MonotonicSeconds - _t0, _o.Clock.UtcNow);
            _session.Markers.Add(m);
            try { WriteSession(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 次の保存でまた試す */ }
            return m;
        }
    }

    public IReadOnlyList<RunMarker> Markers
    {
        get { lock (_gate) return _session?.Markers.ToList() ?? new List<RunMarker>(); }
    }

    /// <summary>止めて、要約を作って、閉じる。何度呼んでもよい。</summary>
    public Task<RunResult> StopAsync(string reason = "user")
    {
        lock (_gate)
        {
            if (!_started) throw new RecordException("state", "not started");
            if (_stopReason.Length == 0) _stopReason = reason;
        }
        _stop.Cancel();
        return _done.Task;
    }

    private void Loop()
    {
        var clock = _o.Clock;
        double interval = _o.Interval.TotalSeconds;
        double max = _o.MaxDuration.TotalSeconds;
        long slot = 1;
        string reason = "";
        int samples = 0;
        double lastT = 0, gap = 0, maxGap = 0;
        try
        {
            while (true)
            {
                clock.WaitUntil(_t0 + slot * interval, _stop.Token);
                if (_stop.IsCancellationRequested) break;

                double now = clock.MonotonicSeconds;
                // 遅れたら、取りこぼした枠は飛ばす（バーストして追いつかない。穴は穴として残る）
                long current = (long)Math.Floor((now - _t0) / interval);
                if (current > slot) slot = current;

                double tRead = clock.MonotonicSeconds - _t0;
                var utc = clock.UtcNow;
                int failed = _hub.ReadInto(_row);
                try { _csv!.WriteRow(tRead, utc, _row); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    reason = "io-error";
                    Trace.TraceWarning("PartsLife: record: write failed: " + ex.GetType().Name);
                    break;
                }

                samples++;
                double dt = tRead - lastT;
                if (samples > 1) { maxGap = Math.Max(maxGap, dt); if (dt > interval * 1.5) gap += dt - interval; }
                lastT = tRead;

                var handler = Sampled;
                if (handler is not null)
                {
                    var values = new Dictionary<string, double>(_row.Count, StringComparer.Ordinal);
                    for (int i = 0; i < _row.Count; i++) if (!double.IsNaN(_row.ValueAt(i))) values[_row.KeyAt(i)] = _row.ValueAt(i);
                    try { handler(new LiveSample(tRead, values, failed)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }

                if (tRead >= max) { reason = "max-duration"; break; }
                slot++;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "error";
            Trace.TraceWarning("PartsLife: record: loop failed: " + ex);
        }

        string stopReason;
        lock (_gate) { stopReason = _stopReason.Length > 0 ? _stopReason : (reason.Length > 0 ? reason : "user"); }
        RunResult result;
        try { result = Finish(stopReason, reason is "io-error" or "error", samples, gap, maxGap); }
        catch (Exception ex)
        {
            _done.TrySetException(ex);
            return;
        }
        _done.TrySetResult(result);
        try { Ended?.Invoke(stopReason); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private RunResult Finish(string stopReason, bool failed, int samples, double gap, double maxGap)
    {
        try { _csv?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _csv = null;

        var session = _session!;
        lock (_gate)
        {
            session.EndUtc = _o.Clock.UtcNow;
            session.Status = failed ? "interrupted" : "completed";
            session.StopReason = stopReason;
            session.Stats = new RunStats { Samples = samples, GapSec = gap, MaxGapSec = maxGap, SourceTimeouts = _hub.TimeoutCounts.ToDictionary(x => x.Key, x => x.Value) };
        }

        RunSummary? summary = null;
        string? summaryError = null;
        try
        {
            var series = CsvStream.Read(_csvPath);
            summary = Summarizer.Summarize(series, session);
            session.Summary = summary;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            summaryError = ex.Message;
        }
        try { WriteSession(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { summaryError ??= ex.Message; }

        _hub.Dispose();
        return new RunResult { Session = session, Summary = summary, JsonPath = _jsonPath, CsvPath = _csvPath, SummaryError = summaryError };
    }

    private static void EnsureFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RecordException("folder", ex.Message, ex);
        }
    }

    private void WriteSession()
    {
        lock (_fileGate)
        {
            RunStore.WriteAtomic(_jsonPath, SessionJson.Write(_session!));
        }
    }

    public void Dispose()
    {
        bool wasStarted;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            wasStarted = _started;
            if (wasStarted && _stopReason.Length == 0) _stopReason = "disposed";
        }
        if (wasStarted)
        {
            _stop.Cancel();
            // 止まり切るのを少し待つ（要約まで作るので、長い記録では数秒かかる）。待ちきれなくても、読み口は糸が閉じる
            _done.Task.Wait(TimeSpan.FromSeconds(15));
        }
        else _hub.Dispose();
    }
}
