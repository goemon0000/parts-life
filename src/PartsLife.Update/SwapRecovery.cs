namespace PartsLife.Update;

public enum RecoveryKind
{
    /// <summary>戻す物（.old）が無い。入れ替えは始まっていなかった（か、もう戻してあった）。記録を片付けるだけ。</summary>
    NothingToRollBack,
    /// <summary>元の版に戻した。</summary>
    Restored,
    /// <summary>戻し切れなかった。記録（試みた回数）は残せたので、起こし直して戻し直す。</summary>
    RetryRelaunch,
    /// <summary>
    /// 戻し切れず、しかも試みた回数を記録に残せなかった（swap.json が読めるのに書けない・消せない）。
    /// **起こし直さない**（起こし直すと、回数の増えない戻しが際限なく続く）。いまの exe のまま動き、知らせる。
    /// </summary>
    Stuck,
    /// <summary>決めた回数試みても戻し切れない。.old と記録を残し、手で戻すよう知らせる。</summary>
    GaveUp,
}

/// <param name="MarkedFailed">その版を「失敗した版」にしたか（新しい版を実際に起こして、うまくいかなかったときだけ）。</param>
public sealed record RecoveryResult(RecoveryKind Kind, bool MarkedFailed, string? Error);

/// <summary>
/// 入れ替えを元に戻す（<see cref="BootAction.Rollback"/> と、見届けで新しい版が起動できなかったとき）。
/// ファイルと覚え書きだけを扱い、起こし直すかどうかは結果を見て呼ぶ側が決める。試験で中身を確かめる。
///
///   - .old が無い → 何もしない（記録を消すだけ）。**その版は動いていないので失敗にしない**（電源が落ちた・後片付けの失敗）
///   - 戻す前に「戻すと決めた・何回目か」を記録に書く。書けなかったら、戻せても戻せなくても起こし直しはさせない
///   - 「失敗した版」にするのは、新しい版を実際に起こした（Launched）ときだけ。起こす前に止まった入れ替えは、次の確認でやり直せる
/// </summary>
public static class SwapRecovery
{
    public static RecoveryResult Rollback(UpdateStore store, string appDir, SwapRecord rec, FileOps? ops = null)
    {
        if (!Swapper.HasOld(appDir, rec.Files))
        {
            store.DeleteSwap();
            if (!rec.Launched) store.DeleteVersionDir(rec.To);   // 起こす前に止まった。落とし直しからやり直す
            return new RecoveryResult(RecoveryKind.NothingToRollBack, false, null);
        }

        rec.RollbackRequested = true;
        rec.RollbackAttempts++;
        bool persisted = store.SaveSwap(rec);

        string? error = Swapper.Rollback(appDir, rec.Files, ops);
        var st = store.LoadState();
        bool markFailed = rec.Launched;
        if (markFailed) st.MarkFailed(rec.To);
        st.LastError = error;

        if (error is null)
        {
            if (markFailed) st.RolledBackVersion = rec.To;
            st.Pending = null;
            store.SaveState(st);
            store.DeleteSwap();
            store.DeleteVersionDir(rec.To);
            return new RecoveryResult(RecoveryKind.Restored, markFailed, null);
        }

        store.SaveState(st);
        if (!persisted) return new RecoveryResult(RecoveryKind.Stuck, markFailed, error);
        if (rec.RollbackAttempts >= SwapBoot.MaxRollbackAttempts) return new RecoveryResult(RecoveryKind.GaveUp, markFailed, error);
        return new RecoveryResult(RecoveryKind.RetryRelaunch, markFailed, error);
    }
}
