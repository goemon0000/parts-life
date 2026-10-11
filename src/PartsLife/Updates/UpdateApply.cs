using System.Diagnostics;
using PartsLife.Game;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 入れ替えて新しい版を起こし、うまく起動したかを見届ける。だめなら元に戻して元の版を起こし直す。
/// 流れの決まりは <see cref="SwapBoot"/>、ファイルの付け替えは <see cref="Swapper"/>、照らし直しは <see cref="UpdatePlan"/>（どれも試験つき）。
///
/// **順番が要点。**
///   1. 入れ替えの印（<see cref="UpdateLock"/>）を握る。握れなければ入れ替えない（ほかで入れ替えの最中）
///   2. swap.json を書いて読み戻す。**書けなければ名前を 1 つも変えずにやめる**（記録の無い入れ替えは、落ちたら戻せない）
///   3. 名前の付け替え。失敗して戻し切れなければ swap.json を残す → 次の起動で戻す
///   4. 「起こすところまで来た」（Launched）を書く。書けなければ、その場で戻してやめる
///   5. 新しい版を起こし、見届ける。見届けられなかったら、**必ず戻してから**元の版を起こし直す
/// </summary>
internal static class UpdateApply
{
    /// <summary>
    /// 用意してある版に入れ替えて、新しい版を <c>--updated-from 古い版</c> で起こす。
    /// 起こせたらその Process と記録（呼んだ側が <see cref="WatchAsync"/> で見届ける）、だめなら null。
    /// </summary>
    public static (Process Child, SwapRecord Record)? SwapAndRelaunch(UpdateRuntime rt, UpdateState st, PendingUpdate p, out string? error)
    {
        if (!rt.Lock.TryAcquire(TimeSpan.Zero))
        {
            error = "ほかで入れ替えの最中";
            return null;
        }
        var result = SwapLocked(rt, st, p, out error);
        if (result is null) rt.Lock.Release();
        return result;
    }

    private static (Process, SwapRecord)? SwapLocked(UpdateRuntime rt, UpdateState st, PendingUpdate p, out string? error)
    {
        error = UpdatePlan.Check(p, rt.Layout, rt.Current, st);
        if (error is not null)
        {
            Drop(rt, st);
            return null;
        }

        var record = new SwapRecord
        {
            From = rt.Current.ToString(),
            To = p.Version,
            Files = p.Files.Select(f => f.Name).ToList(),
            PageUrl = p.PageUrl,
            OldPid = Environment.ProcessId,
            SwappedAtUtc = DateTimeOffset.UtcNow,
        };
        if (!rt.Store.SaveSwap(record))
        {
            error = "更新の記録を書けないので入れ替えません";
            CountFailure(rt, st, p.Version, error);
            return null;
        }

        var outcome = Swapper.Apply(rt.AppDir, rt.Store.FilesDir(p.Version), p.Files);
        if (!outcome.Ok)
        {
            error = outcome.Error;
            // 元のままなら記録を消す。戻し切れなかったら記録（Launched が偽）を残し、次の起動で戻す
            if (!outcome.Dirty) rt.Store.DeleteSwap();
            CountFailure(rt, st, p.Version, error);
            return null;
        }

        record.Launched = true;
        if (!rt.Store.SaveSwap(record))
        {
            // 「起こすところまで来た」を書けない → 新しい版は自分を「途中で止まった入れ替え」と見て戻してしまう。いま戻す
            error = "更新の記録を書けないので元に戻しました";
            if (Swapper.Rollback(rt.AppDir, record.Files) is null) rt.Store.DeleteSwap();
            CountFailure(rt, st, p.Version, error);
            return null;
        }
        st.Pending = null;
        st.ApplyFailureVersion = null;
        st.ApplyFailures = 0;
        rt.Store.SaveState(st);

        var child = rt.Launch("--updated-from", rt.Current.ToString());
        if (child is null)
        {
            var r = SwapRecovery.Rollback(rt.Store, rt.AppDir, record);
            error = r.Kind == RecoveryKind.Restored ? "新しい版を起動できなかったので元に戻しました"
                  : "新しい版を起動できず、元にも戻し切れませんでした（次の起動で戻し直します）: " + r.Error;
            return null;
        }
        return (child, record);
    }

    /// <summary>入れ替えの失敗を数え、置き場は捨てる（次の確認で落とし直す）。同じ版で続けて 3 回なら、その版は入れない。</summary>
    private static void CountFailure(UpdateRuntime rt, UpdateState st, string version, string? error)
    {
        st.LastError = error;
        st.RecordApplyFailure(version);
        Drop(rt, st);
    }

    /// <summary>
    /// 新しい版が「起動できた」（swap.json の Confirmed）と書くのを待つ。書いたら shutdown（この古い版は終わる）。
    /// 新しい版が先に終わった・<see cref="SwapBoot.WatchTimeout"/> 待っても書かない → 止めて、**必ず戻してから**元の版を起こし直す。
    /// swap.json が読めなくなっても、手元の記録で戻す（読めないことを理由に戻さず消える、をしない）。
    /// 戻し切れず、起こし直しもさせられないときは stayHere（この古い版のまま窓を出して知らせる）。
    /// </summary>
    public static async Task WatchAsync(UpdateRuntime rt, Process child, SwapRecord record, Action shutdown, Action<string> stayHere)
    {
        var until = DateTime.UtcNow + SwapBoot.WatchTimeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            if (rt.Store.LoadSwap() is { Confirmed: true }) { rt.Lock.Release(); shutdown(); return; }
            if (child.HasExited) break;
        }
        if (rt.Store.LoadSwap() is { Confirmed: true }) { rt.Lock.Release(); shutdown(); return; }
        try
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        var rec = rt.Store.LoadSwap() ?? record;
        var r = SwapRecovery.Rollback(rt.Store, rt.AppDir, rec);
        rt.Lock.Release();   // 起こし直す版が待たずに印を握れるように
        switch (r.Kind)
        {
            case RecoveryKind.Restored:
            case RecoveryKind.NothingToRollBack:
                // 戻した元の版を起こす（覚え書きを書けなかったときのために、知らせる版を引数でも渡す）
                if (r.MarkedFailed) rt.Launch("--update-rolled-back", rec.To);
                else rt.Launch("--update-restored");
                shutdown();
                break;
            case RecoveryKind.RetryRelaunch:
                rt.Launch();   // 置いてある物を起こす。その起動が戻し直す（回数は記録に残してある）
                shutdown();
                break;
            case RecoveryKind.GaveUp:
                stayHere(I18n.Strings.UpdateGaveUp(rec.To));
                break;
            default:
                stayHere(I18n.Strings.UpdateStuck(r.Error ?? ""));
                break;
        }
    }

    /// <summary>用意を捨てる（置き場も消す）。</summary>
    public static void Drop(UpdateRuntime rt, UpdateState st)
    {
        if (st.Pending is { } p) rt.Store.DeleteVersionDir(p.Version);
        st.Pending = null;
        rt.Store.SaveState(st);
    }
}

/// <summary>起動の最初に決まったこと（窓に渡す）。</summary>
/// <param name="JustUpdated">入れ替えた新しい版として起動した（しばらく無事なら確かめ済みにする）。</param>
/// <param name="GiveUp">戻し切れず、手で戻してもらう状態（その版）。しばらく無事なら記録を消す。</param>
/// <param name="Notice">窓に出すお知らせ。</param>
/// <param name="Busy">入れ替えの最中に別に起動された（窓を出さず、短いお知らせだけ出して終わる）。</param>
internal sealed record BootResult(SwapRecord? JustUpdated = null, string? GiveUp = null, string? Notice = null, bool Busy = false);

/// <summary>
/// 起動の最初（窓を出す前）にやること。
///   - 入れ替えの印（<see cref="UpdateLock"/>）を確かめる。入れ替えの最中に別に起動されたなら、少し待って、空かなければ何もせずに終わる
///   - 入れ替えの記録を見て、確かめの回数を数える・落ち続けていれば戻す・途中で止まった入れ替えを戻す・済んでいれば .old を消す
///   - 「自動で更新する」で用意済みの版があれば、窓を出す前に入れ替えて起こし直す
///     （前回は点検の最中などで入れ替えられないまま終わった場合。窓を出してから替えるより、ちらつかない）
/// </summary>
internal static class UpdateBoot
{
    /// <summary>
    /// 入れ替えの印が空くのを待つ長さ。**画面のスレッドで待つので短く。** 入れ替える側は、起こし直す前に印を放してある。
    /// 空かなければ（入れ替えの最中）、短いお知らせを出して終わる。
    /// </summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(500);

    /// <returns>この起動はここで終わる（入れ替え・戻しのために起こし直す・入れ替えの最中）なら true。</returns>
    public static bool Run(UpdateRuntime rt, PartyState state, string[] args, Action shutdown, Action<string> stayHere, out BootResult result)
    {
        int at = Array.IndexOf(args, "--update-rolled-back");
        string? rolledBack = at >= 0 && at + 1 < args.Length && AppVersion.TryParse(args[at + 1], out _) ? args[at + 1] : null;
        bool restored = rolledBack is not null || Array.IndexOf(args, "--update-restored") >= 0;
        bool spawnedByUpdate = Array.IndexOf(args, "--updated-from") >= 0;
        result = new BootResult(Notice: rolledBack is not null ? I18n.Strings.UpdateRolledBack(rolledBack, rt.Current.ToString()) : null);

        var rec = rt.Store.LoadSwap();
        var action = SwapBoot.Decide(rec, rt.Current, restored);

        // 入れ替えで起こされた新しい版が「確かめ」をするときだけは印を待たない（古い版が握ったまま見届けている）。
        // それ以外は印を握ってから。握れなければ、何にも触らずに終わる（起動の回数にも戻しにも触らない）
        if (!(spawnedByUpdate && action == BootAction.Verify))
        {
            if (!rt.Lock.TryAcquire(LockWait))
            {
                result = new BootResult(Busy: true);
                return true;
            }
            rec = rt.Store.LoadSwap();   // 待っている間に変わったかもしれない
            action = SwapBoot.Decide(rec, rt.Current, restored);
        }

        try
        {
            switch (action)
            {
                case BootAction.Rollback:
                {
                    var r = SwapRecovery.Rollback(rt.Store, rt.AppDir, rec!);
                    rt.Lock.Release();
                    bool iAmOld = rt.Current.ToString() == rec!.From;
                    switch (r.Kind)
                    {
                        case RecoveryKind.NothingToRollBack:
                            break;   // 入れ替えは始まっていなかった。このまま普通に起動する
                        case RecoveryKind.Restored when iAmOld:
                            result = result with
                            {
                                Notice = r.MarkedFailed ? I18n.Strings.UpdateRolledBack(rec.To, rt.Current.ToString()) : I18n.Strings.UpdateRestored,
                            };
                            break;   // 戻した物がこの版。このまま起動する
                        case RecoveryKind.Restored:
                            if (r.MarkedFailed) rt.Launch("--update-rolled-back", rec.To);
                            else rt.Launch("--update-restored");
                            shutdown();
                            return true;
                        case RecoveryKind.RetryRelaunch:
                            rt.Launch();
                            shutdown();
                            return true;
                        case RecoveryKind.GaveUp:
                            result = result with { GiveUp = rec.To, Notice = I18n.Strings.UpdateGaveUp(rec.To) };
                            return false;   // .old と記録は残す
                        default:
                            // 試みた回数を残せない。起こし直すと際限なく続くので、この exe のまま起動して知らせる
                            result = result with { Notice = I18n.Strings.UpdateStuck(r.Error ?? "") };
                            return false;
                    }
                    break;
                }
                case BootAction.GiveUp:
                    result = result with { GiveUp = rec!.To, Notice = I18n.Strings.UpdateGaveUp(rec.To) };
                    return false;   // .old と記録は残す（手で戻せるように）
                case BootAction.Hold:
                    return false;   // 起こし直された直後。戻しも片付けもしない
                case BootAction.Verify:
                    rec!.Launches++;
                    rt.Store.SaveSwap(rec);
                    result = result with { JustUpdated = rec };
                    return false;   // .old は確かめが済むまで残す
                case BootAction.Cleanup:
                    Swapper.Cleanup(rt.AppDir, rec!.Files);
                    rt.Store.DeleteSwap();
                    rt.Store.DeleteStagedUpTo(rt.Current);
                    break;
                case BootAction.Stale:
                    rt.Store.DeleteSwap();
                    Swapper.Cleanup(rt.AppDir, UpdateRuntime.AllFiles);
                    break;
                default:
                    if (SwapBoot.MayCleanup(action)) Swapper.Cleanup(rt.AppDir, UpdateRuntime.AllFiles);
                    break;
            }

            var st = rt.Store.LoadState();
            if (st.Pending is not { } p) return false;
            if (UpdateModes.Parse(state.UpdateMode) != UpdateMode.Auto || !rt.CanReplace)
            {
                UpdateApply.Drop(rt, st);
                return false;
            }
            var started = UpdateApply.SwapAndRelaunch(rt, st, p, out _);
            if (started is not { } s) return false;
            _ = UpdateApply.WatchAsync(rt, s.Child, s.Record, shutdown, stayHere);
            return true;
        }
        finally
        {
            // 入れ替えて見届けている間は握ったまま（終わるときに放される）。それ以外はここで放す
            if (!(rt.Store.LoadSwap() is { Confirmed: false, Launched: true } r && r.OldPid == Environment.ProcessId))
                rt.Lock.Release();
        }
    }
}
