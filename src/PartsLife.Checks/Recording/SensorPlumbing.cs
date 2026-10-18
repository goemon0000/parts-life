namespace PartsLife.Checks.Recording;

/// <summary>
/// 1 秒ぶんの値の入れ物。列（項目の名前）は検出のあとで決まり、読めなかった値は NaN のまま。
/// </summary>
public sealed class SampleRow
{
    private readonly string[] _keys;
    private readonly double[] _values;
    private readonly Dictionary<string, int> _index;

    public SampleRow(IEnumerable<string> keys)
    {
        _keys = keys.ToArray();
        _values = new double[_keys.Length];
        _index = new Dictionary<string, int>(_keys.Length, StringComparer.Ordinal);
        for (int i = 0; i < _keys.Length; i++) _index[_keys[i]] = i;
        Clear();
    }

    public int Count => _keys.Length;
    public string KeyAt(int i) => _keys[i];
    public double ValueAt(int i) => _values[i];
    public bool Has(string key) => _index.ContainsKey(key);

    public void Clear() => Array.Fill(_values, double.NaN);

    /// <summary>列に無い項目は黙って捨てる（検出で読めないと判った項目を、読む側が書こうとしても害が無いように）。</summary>
    public void Set(string key, double value)
    {
        if (_index.TryGetValue(key, out int i)) _values[i] = double.IsFinite(value) ? value : double.NaN;
    }

    public double Get(string key) => _index.TryGetValue(key, out int i) ? _values[i] : double.NaN;

    /// <summary>other にある値（NaN でない物）を、同じ名前の列へ写す。</summary>
    public void CopyFrom(SampleRow other)
    {
        for (int i = 0; i < other._keys.Length; i++) Set(other._keys[i], other._values[i]);
    }
}

/// <summary>
/// センサーの読み口 1 つ（CPU のカウンタ、NVIDIA のドライバ、メモリ …）。
///
/// ■ 呼び方の決まり
/// <see cref="Detect"/> は開始のとき 1 回。責任を持つ項目を**全部**返す（読めない物も、理由つきで）。
/// <see cref="Prime"/> は記録の直前に 1 回（PDH のように差分で値を出す読み口の基準を取る）。
/// <see cref="Read"/> は毎秒。Detect が Available とした項目の値だけを書く。
/// 3 つとも同じ糸から呼ばれる（<see cref="SourceRunner"/> の専用の糸）。
///
/// ■ 読み出しだけ
/// 設定を変える・ファンを回す・電圧に触る処理は、この仕組みには置かない。
/// </summary>
public interface ISensorSource : IDisposable
{
    string Id { get; }
    IReadOnlyList<MetricStatus> Detect();
    void Prime();
    void Read(SampleRow row);
}

/// <summary>読み口の束。GPU の素性（記録の中の番号と実体の対応）も一緒に持つ。閉じるのは SensorHub（持ち主が移る）。</summary>
public sealed class SourceSet
{
    public IReadOnlyList<ISensorSource> Sources { get; }
    public IReadOnlyList<GpuDescriptor> Gpus { get; }
    public int LogicalProcessors { get; }

    public SourceSet(IReadOnlyList<ISensorSource> sources, IReadOnlyList<GpuDescriptor>? gpus = null, int logicalProcessors = 0)
    {
        Sources = sources;
        Gpus = gpus ?? Array.Empty<GpuDescriptor>();
        LogicalProcessors = logicalProcessors > 0 ? logicalProcessors : Environment.ProcessorCount;
    }
}

/// <summary>時計。試験では差し替えて、待たずに時間を進める。</summary>
public interface IRunClock
{
    /// <summary>単調な時計（止まったり戻ったりしない）。記録の開始からではなく、起動からの秒でよい。</summary>
    double MonotonicSeconds { get; }
    DateTime UtcNow { get; }
    /// <summary>単調な時計が target 秒になるまで待つ。<paramref name="cancel"/> が立ったら早く戻る。</summary>
    void WaitUntil(double target, CancellationToken cancel);
}

public sealed class SystemRunClock : IRunClock
{
    public static readonly SystemRunClock Instance = new();
    public double MonotonicSeconds => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public DateTime UtcNow => DateTime.UtcNow;

    public void WaitUntil(double target, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            double left = target - MonotonicSeconds;
            if (left <= 0) return;
            // 待つのは取り消しの合図が立つまで（立てば即座に戻る）。長い待ちは 250 ms ずつに刻む
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(left, 0.25)));
        }
    }
}

/// <summary>
/// 読み口 1 つを専用の糸で動かし、**決めた時間で見切る**。ドライバが固まって戻らなくても、記録の糸は止まらない。
///
/// - 前の読み取りがまだ戻っていないときは、新しい読み取りを始めずに「読めなかった」を返す（固まった DLL に糸を送り込まない）
/// - 中に糸がいる間は、読み口を解放しない（<see cref="Dispose"/> は漏らす。Nvml.Dispose が自分で見張る）
/// 仕組みは点検の TimedReader と同じ。値を持たず、仕事（Action）を実行する点だけが違う。
/// </summary>
internal sealed class SourceRunner : IDisposable
{
    public enum Outcome { Done, Timeout, Busy, Faulted, Closed }

    private readonly object _gate = new();
    private readonly Thread _thread;
    private Action? _job;
    private long _requested, _completed;
    private bool _quit;
    private Exception? _error;

    public ISensorSource Source { get; }
    public SampleRow? Buffer { get; set; }
    public int Timeouts { get; private set; }
    /// <summary>前の読み取りが戻っていなかったので、始めずに飛ばした回数。</summary>
    public int Skipped { get; private set; }
    public Exception? LastError => _error;

    public SourceRunner(ISensorSource source)
    {
        Source = source;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PartsLife.Record." + source.Id };
        _thread.Start();
    }

    public bool Busy { get { lock (_gate) return _completed < _requested; } }

    /// <summary>仕事を 1 つ始める。前の仕事が戻っていない・閉じた後なら false（始めない）。</summary>
    public bool TryPost(Action job, out long ticket)
    {
        lock (_gate)
        {
            ticket = 0;
            if (_quit) return false;
            if (_completed < _requested) { Skipped++; return false; }
            _error = null;
            _job = job;
            ticket = ++_requested;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>始めた仕事の終わりを timeout まで待つ。間に合わなければ Timeout（仕事は裏で続くことがある）。</summary>
    public Outcome Wait(long ticket, TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_completed < ticket)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    if (_completed < ticket) { Timeouts++; return Outcome.Timeout; }
                }
            }
            return _error is null ? Outcome.Done : Outcome.Faulted;
        }
    }

    /// <summary>仕事を 1 つ実行して待つ（検出と基準取りに使う）。</summary>
    public Outcome Run(Action job, TimeSpan timeout) =>
        TryPost(job, out long ticket) ? Wait(ticket, timeout) : (Busy ? Outcome.Busy : Outcome.Closed);

    private void Loop()
    {
        long next = 1;
        while (true)
        {
            Action job;
            lock (_gate)
            {
                while (_requested < next && !_quit) Monitor.Wait(_gate);
                if (_quit && _requested < next) return;
                job = _job!;
            }
            Exception? err = null;
            try { job(); } catch (Exception ex) { err = ex; }
            lock (_gate)
            {
                _error = err;
                _completed = next++;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_quit) return;
            _quit = true;
            Monitor.PulseAll(_gate);
        }
        // 読み取りの途中の糸がいるときは、読み口を閉じずに手放す（解放された場所で動き出さないように）。待たない
        if (!Busy)
        {
            try { Source.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}
