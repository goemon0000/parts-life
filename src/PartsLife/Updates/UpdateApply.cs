using System.Diagnostics;
using PartsLife.Game;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 入れ替えの後始末の一部（用意を捨てる）。流れの決まりは <see cref="SwapBoot"/>、ファイルの付け替えは <see cref="Swapper"/>（どちらも試験つき）。
///
/// **v0.8.0 は入れ替えをしない。** v0.7.4 → v0.8.0 の入れ替えは、出荷済みの v0.7.4 の更新が行う（入れ替え・起こす・見届ける・戻す）。
/// この版に残してあるのは、起こされた子の側（<c>--updated-from</c>・Confirmed）と、途中で止まった入れ替えの後始末・戻し（<see cref="UpdateBoot"/>）だけ。
/// 「用意済みの版を入れ替えて起こす」道（SwapAndRelaunch・WatchAsync）は、ここに無い（試験で固定: NoUpdateCheckWiringTests）。
/// </summary>
internal static class UpdateApply
{
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
///   - **用意済みの版の入れ替えはしない**（v0.8.0 は新しい版を確かめに行かないので、用意される物も無い。以前はここで入れ替えて起こし直していた）
///   - 戻しのために別の PartsLife.exe を起こすときは、その前に常駐の Mutex を放す（<c>releaseResident</c>。起こされた側が握れるように）
/// </summary>
internal static class UpdateBoot
{
    /// <summary>
    /// 入れ替えの印が空くのを待つ長さ。**画面のスレッドで待つので短く。** 入れ替える側は、起こし直す前に印を放してある。
    /// 空かなければ（入れ替えの最中）、短いお知らせを出して終わる。
    /// </summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(500);

    /// <returns>この起動はここで終わる（戻しのために起こし直す・入れ替えの最中）なら true。</returns>
    /// <param name="releaseResident">別の PartsLife.exe を起こす前に、常駐の Mutex を放す（画面のスレッドで呼ぶ）。</param>
    public static bool Run(UpdateRuntime rt, string[] args, Action shutdown, Action releaseResident, out BootResult result)
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
                            releaseResident();   // 起こす物が 0.8.0 なら Mutex を握る。先に放す
                            if (r.MarkedFailed) rt.Launch("--update-rolled-back", rec.To);
                            else rt.Launch("--update-restored");
                            shutdown();
                            return true;
                        case RecoveryKind.RetryRelaunch:
                            releaseResident();
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

            return false;   // 普通に起動する（用意済みの版の入れ替えはしない）
        }
        finally
        {
            rt.Lock.Release();
        }
    }
}
