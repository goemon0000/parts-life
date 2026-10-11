using System.Diagnostics;
using PartsLife.Game;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 入れ替えて新しい版を起こし、うまく起動したかを見届ける。だめなら元に戻して元の版を起こし直す。
/// 流れの決まりは <see cref="SwapBoot"/>、ファイルの付け替えは <see cref="Swapper"/>（どちらも試験つき）。
/// </summary>
internal static class UpdateApply
{
    /// <summary>
    /// 用意してある版に入れ替えて、新しい版を <c>--updated-from 古い版</c> で起こす。
    /// 起こせたらその Process（呼んだ側が <see cref="WatchAsync"/> で見届ける）、だめなら null（フォルダは元のまま）。
    /// </summary>
    public static Process? SwapAndRelaunch(UpdateRuntime rt, UpdateState st, PendingUpdate p, out string? error)
    {
        error = null;
        if (InstallLayouts.Parse(p.Layout) != rt.Layout || !AppVersion.TryParse(p.Version, out var to) || to <= rt.Current)
        {
            error = "用意した版がいまの置き方・版と合わない";
            Drop(rt, st);
            return null;
        }

        error = Swapper.Apply(rt.AppDir, rt.Store.FilesDir(p.Version), p.Files);
        if (error is not null)
        {
            // 一時的な事情（ウイルス対策ソフトが掴んでいる等）もあるので、失敗した版には数えない。
            // 用意は捨てる（次の確認で落とし直す。ここで捨てないと、起動のたびに同じ失敗を繰り返す）
            st.LastError = error;
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
        rt.Store.SaveSwap(record);
        st.Pending = null;
        rt.Store.SaveState(st);

        var child = rt.Launch("--updated-from", rt.Current.ToString());
        if (child is null)
        {
            Swapper.Rollback(rt.AppDir, record.Files);
            rt.Store.DeleteSwap();
            error = "新しい版を起動できなかったので元に戻した";
            st.LastError = error;
            st.MarkFailed(p.Version);
            rt.Store.SaveState(st);
            return null;
        }
        return child;
    }

    /// <summary>
    /// 新しい版が「起動できた」（swap.json の Confirmed）と書くのを待つ。書いたら shutdown（この古い版は終わる）。
    /// 新しい版が先に終わった・<see cref="SwapBoot.WatchTimeout"/> 待っても書かない → 止めて元に戻し、元の版を起こし直す。
    /// </summary>
    public static async Task WatchAsync(UpdateRuntime rt, Process child, Action shutdown)
    {
        var until = DateTime.UtcNow + SwapBoot.WatchTimeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            if (rt.Store.LoadSwap() is { Confirmed: true }) { shutdown(); return; }
            if (child.HasExited) break;
        }
        var rec = rt.Store.LoadSwap();
        if (rec is { Confirmed: true }) { shutdown(); return; }
        try
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (rec is not null) RollbackAndRelaunch(rt, rec, shutdown);
        else shutdown();
    }

    /// <summary>新しい版を退けて元の版に戻し、その版を起こして、自分は終わる。戻した版は二度と入れない。</summary>
    public static void RollbackAndRelaunch(UpdateRuntime rt, SwapRecord rec, Action shutdown)
    {
        string? error = Swapper.Rollback(rt.AppDir, rec.Files);
        var st = rt.Store.LoadState();
        st.MarkFailed(rec.To);
        st.RolledBackVersion = rec.To;
        st.LastError = error;
        rt.Store.SaveState(st);
        rt.Store.DeleteSwap();
        rt.Store.DeleteVersionDir(rec.To);
        rt.Launch();
        shutdown();
    }

    /// <summary>用意を捨てる（置き場も消す）。</summary>
    public static void Drop(UpdateRuntime rt, UpdateState st)
    {
        if (st.Pending is { } p) rt.Store.DeleteVersionDir(p.Version);
        st.Pending = null;
        rt.Store.SaveState(st);
    }
}

/// <summary>
/// 起動の最初（窓を出す前）にやること。
///   - 入れ替えの記録を見て、確かめの回数を数える・落ち続けていれば戻す・済んでいれば .old を消す
///   - 「自動で更新する」で用意済みの版があれば、窓を出す前に入れ替えて起こし直す
///     （前回は点検の最中などで入れ替えられないまま終わった場合。窓を出してから替えるより、ちらつかない）
/// </summary>
internal static class UpdateBoot
{
    /// <returns>この起動はここで終わる（入れ替え・戻しのために起こし直す）なら true。</returns>
    public static bool Run(UpdateRuntime rt, PartyState state, Action shutdown, out SwapRecord? justUpdated)
    {
        justUpdated = null;
        var rec = rt.Store.LoadSwap();
        switch (SwapBoot.Decide(rec, rt.Current))
        {
            case BootAction.Rollback:
                UpdateApply.RollbackAndRelaunch(rt, rec!, shutdown);
                return true;
            case BootAction.Verify:
                rec!.Launches++;
                rt.Store.SaveSwap(rec);
                justUpdated = rec;
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
                Swapper.Cleanup(rt.AppDir, UpdateRuntime.AllFiles);
                break;
        }

        var st = rt.Store.LoadState();
        if (st.Pending is not { } p) return false;
        if (UpdateModes.Parse(state.UpdateMode) != UpdateMode.Auto || !rt.CanReplace
            || !AppVersion.TryParse(p.Version, out var v) || v <= rt.Current || st.HasFailed(v))
        {
            UpdateApply.Drop(rt, st);
            return false;
        }
        var child = UpdateApply.SwapAndRelaunch(rt, st, p, out _);
        if (child is null) return false;
        _ = UpdateApply.WatchAsync(rt, child, shutdown);
        return true;
    }
}
