namespace PartsLife.Checks.Gpu;

/// <summary>
/// 温度の読み口を、**決めた時間で見切る**ための包み。読み取りは専用の糸1本で順に行う。
///
/// ■ なぜ要るか
/// nvml.dll の読み取りが戻ってこない（ドライバが固まる）と、見張りの糸ごと止まり、
/// 負荷を止める者がいなくなる。そこで呼ぶ側は <see cref="TryRead"/> で待つ時間を区切り、
/// 間に合わなければ「読めなかった」として先へ進む。
/// ■ 固まった読み取りは追い越さない
/// 前の読み取りがまだ戻っていない間は、新しい読み取りを始めずに即座に「読めなかった」を返す
/// （固まった DLL に糸を次々と送り込まない）。<see cref="Busy"/> が true の間は、
/// 糸が DLL の中にいるかもしれないので、**その DLL を解放してはいけない。**
/// </summary>
internal sealed class TimedReader : IDisposable
{
    private readonly Func<int?> _read;
    private readonly object _gate = new();
    private long _requested, _completed;
    private int? _value;
    private bool _quit;

    public TimedReader(Func<int?> read, string name)
    {
        _read = read;
        new Thread(Loop) { IsBackground = true, Name = name }.Start();
    }

    /// <summary>読み取りが始まったまま戻っていない（糸が読み口の中にいる）。</summary>
    public bool Busy
    {
        get { lock (_gate) return _completed < _requested; }
    }

    /// <summary>
    /// 1回読む。<paramref name="timeout"/> までに戻れば true と値（読み口が null を返せば null）。
    /// 間に合わない・前の読み取りがまだ戻っていない・閉じた後なら false。
    /// </summary>
    public bool TryRead(TimeSpan timeout, out int? value)
    {
        value = null;
        lock (_gate)
        {
            if (_quit || _completed < _requested) return false;
            long mine = ++_requested;
            Monitor.PulseAll(_gate);
            var deadline = DateTime.UtcNow + timeout;
            while (_completed < mine)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    if (_completed < mine) return false;
                }
            }
            value = _value;
            return true;
        }
    }

    private void Loop()
    {
        long next = 1;
        while (true)
        {
            lock (_gate)
            {
                while (_requested < next && !_quit) Monitor.Wait(_gate);
                if (_quit) return;
            }
            int? v = null;
            try { v = _read(); } catch { }
            lock (_gate)
            {
                _value = v;
                _completed = next++;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>糸に終わるよう伝える。読み取りの途中なら、戻ってきたところで終わる（待たない）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _quit = true;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>
/// 「いま DLL の中に糸がいるか」を数え、**中に誰かいる間は解放させない**ための小さな関所。
/// 入る側: <see cref="TryEnter"/> → 呼ぶ → <see cref="Exit"/>。閉じる側: <see cref="TryClose"/>。
/// 閉じた後は誰も入れない。閉じるときに中に誰かいれば false（解放してはいけない）。
/// </summary>
internal sealed class InsideGuard
{
    private int _inside;
    private int _closed;

    public bool TryEnter()
    {
        Interlocked.Increment(ref _inside);
        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Decrement(ref _inside);
            return false;
        }
        return true;
    }

    public void Exit() => Interlocked.Decrement(ref _inside);

    /// <summary>以後の入場を断る。中に誰もいなければ true（解放してよい）。2回目以降は false。</summary>
    public bool TryClose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return false;
        return Volatile.Read(ref _inside) == 0;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;
}
