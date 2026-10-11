namespace PartsLife.Update;

/// <summary>起動したときに、入れ替えの記録（swap.json）を見て何をするか。</summary>
public enum BootAction
{
    /// <summary>記録なし。残っている .old などを片付けて、普通に起動する。</summary>
    None,
    /// <summary>入れ替えた新しい版として起動した。起動の回数を数え、しばらく無事に動いたら確かめ済みにする。</summary>
    Verify,
    /// <summary>新しい版が確かめの済む前に何度も起動している（落ち続けている）。元の版に戻す。</summary>
    Rollback,
    /// <summary>確かめ済み。.old を消して記録を片付ける（「次にうまく起動したときに .old を消す」）。</summary>
    Cleanup,
    /// <summary>記録が今の版と合わない（戻した後など）。記録だけ消す。</summary>
    Stale,
    /// <summary>
    /// 何度戻そうとしても戻し切れない。利用者に手で戻すよう知らせる。**.old と記録は残す**
    /// （片付けで .old を消すと、手で戻す物が無くなる）。いまの版がしばらく無事に動いたら記録を消す。
    /// </summary>
    GiveUp,
    /// <summary>戻して起こし直された直後（--update-rolled-back / --update-restored）。戻しも片付けもしない（戻しの輪を作らない）。</summary>
    Hold,
}

/// <summary>
/// 入れ替えの後の起動の決まり。**純粋な関数**（試験で中身を確かめる）。
///
/// 入れ替えの流れ:
///   1. 古い版が入れ替えて swap.json を書き、新しい版を <c>--updated-from 古い版</c> で起こす
///   2. 古い版は窓を隠して待つ（<see cref="WatchTimeout"/> まで）
///   3. 新しい版は窓を出し、<see cref="HealthyAfter"/> 無事に動いたら Confirmed を書く
///   4. 古い版はそれを見て終わる。新しい版は古い版が終わるのを待って .old を消す
/// 新しい版が途中で終わった・時間内に Confirmed を書かなかった → 古い版が戻して、元の版を起こし直す。
/// 古い版が見張れなかった（電源が落ちた等）ときは、新しい版の起動の回数で見る（<see cref="MaxUnconfirmedLaunches"/>）。
/// </summary>
public static class SwapBoot
{
    /// <summary>新しい版が窓を出してから、これだけ無事に動けば「起動できた」とする。</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(15);

    /// <summary>古い版が待つ時間。遅い HDD でも単一ファイルの展開と <see cref="HealthyAfter"/> が収まる長さ。</summary>
    public static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(120);

    /// <summary>確かめが済まないまま、この回数目の起動になったら戻す。</summary>
    public const int MaxUnconfirmedLaunches = 3;

    /// <summary>戻しをこの回数試みても戻し切れなければ諦める（起こし直しが際限なく続かないように）。</summary>
    public const int MaxRollbackAttempts = 3;

    /// <param name="relaunchedAfterRollback">戻して起こし直された起動（--update-rolled-back / --update-restored）。</param>
    public static BootAction Decide(SwapRecord? record, AppVersion running, bool relaunchedAfterRollback = false)
    {
        var action = DecideCore(record, running);
        return relaunchedAfterRollback && action is BootAction.Rollback ? BootAction.Hold : action;
    }

    /// <summary>.old・.failed を片付けてよいか。戻す・諦めて手で戻してもらう・起こし直された直後・確かめの途中では片付けない。</summary>
    public static bool MayCleanup(BootAction action) => action is BootAction.None or BootAction.Stale or BootAction.Cleanup;

    private static BootAction DecideCore(SwapRecord? record, AppVersion running)
    {
        if (record is null) return BootAction.None;
        if (record.Confirmed) return BootAction.Cleanup;
        // 入れ替えの途中で止まった（新しい版を起こすところまで行かなかった）・戻すと決めてまだ戻し切れていない。
        // **どの版が起きても戻す。** ここで .old を片付けると、古い版が二度と戻らない
        if (!record.Launched || record.RollbackRequested)
            return record.RollbackAttempts >= MaxRollbackAttempts ? BootAction.GiveUp : BootAction.Rollback;
        if (!AppVersion.TryParse(record.To, out var to) || to != running) return BootAction.Stale;
        return record.Launches + 1 >= MaxUnconfirmedLaunches ? BootAction.Rollback : BootAction.Verify;
    }
}

/// <summary>
/// 入れ替えの最中の印。名前つきの Mutex（利用者ごと: <c>Local\PartsLife.Update</c>）。
///   - 入れ替える側（古い版）は、名前を付け替える前から見届けが終わるまで握る
///   - 入れ替えの最中に別に起動されたら（二重クリック・ログオン時の自動起動）、少し待って、空かなければ何もせずに終わる。
///     起動の回数（Launches）にも戻しにも触らない
///   - 入れ替えで起こされた新しい版（<c>--updated-from</c>）だけは待たない（古い版が握ったまま見届けているため）
/// Mutex は握ったスレッドでしか放せない。このアプリでは画面のスレッドだけで握り、放す。
/// </summary>
public sealed class UpdateLock : IDisposable
{
    public const string DefaultName = @"Local\PartsLife.Update";

    private readonly Mutex _mutex;
    private bool _held;

    public UpdateLock(string name = DefaultName) => _mutex = new Mutex(false, name);

    public bool IsHeld => _held;

    /// <summary>握る（もう握っていれば真）。timeout まで空かなければ偽。</summary>
    public bool TryAcquire(TimeSpan timeout)
    {
        if (_held) return true;
        try { _held = _mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { _held = true; }   // 握っていた側が落ちた。引き継ぐ
        return _held;
    }

    public void Release()
    {
        if (!_held) return;
        _held = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
    }
}

/// <summary>入れ替える直前に、用意した物を照らし直す。**純粋な関数**（試験で中身を確かめる）。</summary>
public static class UpdatePlan
{
    /// <returns>入れ替えてよければ null、だめなら理由。</returns>
    public static string? Check(PendingUpdate p, InstallLayout layout, AppVersion current, UpdateState state)
    {
        if (InstallLayouts.Parse(p.Layout) != layout) return "用意した版と、いまの置き方が合わない";
        if (!AppVersion.TryParse(p.Version, out var v)) return "用意した版の数が読めない";
        if (v <= current) return "用意した版がいまより新しくない";
        if (state.HasFailed(v)) return "前にうまく起動しなかった版";
        // 入れ替える名前は、置き方の決まりの物ちょうど（順番も）。覚え書きを書き換えられても、ほかの名前には触らない
        var want = InstallLayouts.FilesFor(layout);
        if (p.Files.Count != want.Count) return "入れ替える物の数が合わない";
        for (int i = 0; i < want.Count; i++)
        {
            if (!string.Equals(p.Files[i].Name, want[i], StringComparison.Ordinal)) return $"入れ替える物の名前が違う（{p.Files[i].Name}）";
            if (!Sha256List.IsHex64(p.Files[i].Sha256)) return $"{want[i]} の値が無い";
        }
        return null;
    }
}
