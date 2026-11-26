using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PartsLife.Game;
using PartsLife.I18n;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 常駐の窓と更新の繋ぎ。
///
/// **v0.8.0（最後の版）では、新しい版を確かめに行かない。** 最初の確認・定期の確認のタイマーは動かさず、
/// 手動の確認（「今すぐ確認」）も無い（代わりに「PCミエルのお知らせをもう一度見る」）。通信は一切しない。
/// 残してあるのは、v0.7.4 → v0.8.0 の入れ替えが成り立つための物:
///   - 入れ替えで起こされた新しい版（<c>--updated-from</c>）の「起動できた」（Confirmed）の記録（<see cref="ConfirmStarted"/>・<see cref="OnAppClosing"/>）
///   - 起動の最初の入れ替えの後始末・戻し（<see cref="UpdateBoot"/>。窓を出す前。App.OnStartup）
/// 一度だけの案内（<see cref="FinalNoticeUi"/>）は、主画面を出したあと。入れ替えで起こされたときは Confirmed を書き終えてから。
/// </summary>
internal sealed class UpdateController
{
    private readonly MainWindow _host;
    private readonly PartyState _state;
    private readonly UpdateRuntime _rt;
    private SwapRecord? _justUpdated;
    private readonly BootResult _boot;

    /// <summary>
    /// 案内（PCミエルのお知らせ）を出してよいか。入れ替えの確かめの最中（Confirmed を書き終えるまで）は偽。入れ替えが無い起動では最初から真。
    /// 偽の間は、献立・設定の「もう一度見る」も何もしない（確かめの途中で、案内を開いたり既読にしたりしない）。
    /// Confirmed を書けなかったときは、この実行では偽のまま（古い版が戻すので、案内は新しい版の次の機会に）。
    /// </summary>
    private bool _noticeReady;

    /// <summary>Confirmed を書いている最中（画面のスレッドの外）。窓が閉じられたら、これが済むのを待つ。</summary>
    private Task<ConfirmOutcome>? _confirming;

    public bool NoticeReady => _noticeReady;

    private readonly DispatcherTimer _healthy = new();

    public UpdateController(MainWindow host, PartyState state, UpdateRuntime rt, BootResult boot)
    {
        _host = host;
        _state = state;
        _rt = rt;
        _boot = boot;
        _justUpdated = boot.JustUpdated;
        _noticeReady = boot.JustUpdated is null;

        _healthy.Interval = SwapBoot.HealthyAfter;
        _healthy.Tick += (_, _) => { _healthy.Stop(); ConfirmStarted(); };
    }

    private UpdateMode Mode => UpdateModes.Parse(_state.UpdateMode);

    /// <summary>窓が出たら呼ぶ。</summary>
    public void Start()
    {
        // 確かめの途中（入れ替えた新しい版）・諦めて手で戻してもらう状態のときは、しばらく無事に動いたら記録を片付ける
        if (_justUpdated is not null || _boot.GiveUp is not null) _healthy.Start();
        ShowBootNotice();
        // 入れ替えの確かめの最中でなければ、案内はいま出す。確かめの最中なら、Confirmed を書き終えたあと（ConfirmStarted）
        if (_justUpdated is null) FinalNoticeUi.ShowIfUnread(_host);
    }

    /// <summary>
    /// 見届けで新しい版がだめだったが、戻し切れず起こし直しもできない（またはもう諦めた）。この古い版のまま窓を戻して知らせる。
    /// </summary>
    public void ResumeAfterRollback(string notice)
    {
        _host.ResumeAfterFailedUpdate();
        _host.ShowNotice(notice, null, null);
    }

    /// <summary>
    /// 窓が閉じられた（利用者が終了を選んだ）。入れ替えた直後の確かめの最中なら、ここで「起動できた」と書く。
    /// **普通に閉じられたのは落ちたのとは違う。** 書かずに終わると、見張っている古い版が「起動できなかった」と取って元に戻してしまう。
    /// .old は次の起動で消す（<see cref="BootAction.Cleanup"/>）。
    /// </summary>
    public void OnAppClosing()
    {
        if (_confirming is { } writing)
        {
            // 画面のスレッドの外で書いている最中。済むのを少し待つ（待たずに終わると、見張っている古い版が戻してしまう）
            bool finished = false;
            try { finished = writing.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { }
            if (!finished || (writing.IsCompletedSuccessfully && writing.Result != ConfirmOutcome.Failed)) return;
            // 書けなかった。閉じる前に最後にもう一度（案内は出さない）
        }
        else if (_justUpdated is null || !_healthy.IsEnabled) return;
        _healthy.Stop();
        _justUpdated = null;
        SwapConfirm.TryConfirm(_rt.Store, _rt.Current.ToString(), out _);   // 失敗しても、戻すのは古い版に任せる
    }

    public void Stop()
    {
        _healthy.Stop();
    }

    // -----------------------------------------------------------------------
    // 起動の確かめ（入れ替えた新しい版として起こされたとき）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 入れ替えた新しい版として、窓を出してしばらく無事に動いた。「起動できた」と書き、古い版が終わるのを待って .old を消す。
    /// </summary>
    private void ConfirmStarted()
    {
        if (_boot.GiveUp is not null && _justUpdated is null)
        {
            // 諦めた後、いまの版が無事に動いた。記録を消す（次の起動から .old の片付けに戻る）
            if (_rt.Store.LoadSwap() is { } stuck && stuck.RollbackAttempts >= SwapBoot.MaxRollbackAttempts) _rt.Store.DeleteSwap();
            return;
        }
        _justUpdated = null;
        var store = _rt.Store;
        string current = _rt.Current.ToString();
        var dispatcher = _host.Dispatcher;
        // 書く（読み戻しまで・5 回まで再試行）のは画面のスレッドの外。窓を止めない。書き終えたら画面のスレッドへ戻して案内を出す
        _confirming = Task.Run(() =>
        {
            var outcome = SwapConfirm.TryConfirm(store, current, out var rec);
            dispatcher.BeginInvoke(new Action(() => OnConfirmed(outcome, rec)));
            return outcome;
        });
    }

    /// <summary>
    /// Confirmed の書き込みが終わった（画面のスレッド）。**書けたときだけ**案内を出す。書けなかったら出さない
    /// （古い版が、見張りの時間切れで元に戻す。戻る版に「PCミエルの一部になりました」を既読にさせない）。
    /// </summary>
    private void OnConfirmed(ConfirmOutcome outcome, SwapRecord? rec)
    {
        _confirming = null;
        if (outcome == ConfirmOutcome.Failed) return;
        _noticeReady = true;
        if (outcome == ConfirmOutcome.Confirmed && rec is not null)
        {
            ShowWithLink(Strings.UpdateUpdated(rec.To), rec.PageUrl, TimeSpan.FromMinutes(10));
            StartCleanup(rec);
        }
        FinalNoticeUi.ShowIfUnread(_host);   // 「起動できた」を書き終えてから。一度だけの案内
        _host.RefreshUpdateSettings();
    }

    /// <summary>古い版が終わるのを待って、.old を消す。</summary>
    private void StartCleanup(SwapRecord rec)
    {
        var rt = _rt;
        _ = Task.Run(() =>
        {
            try
            {
                using var old = Process.GetProcessById(rec.OldPid);
                old.WaitForExit(60_000);
            }
            catch (ArgumentException) { /* もう終わっている */ }
            catch (InvalidOperationException) { }
            Swapper.Cleanup(rt.AppDir, rec.Files);   // 消せなかった物は次の起動で
            rt.Store.DeleteSwap();
            rt.Store.DeleteStagedUpTo(rt.Current);
        });
    }

    private void ShowBootNotice()
    {
        var st = _rt.Store.LoadState();
        string? n = _boot.Notice;
        if (st.RolledBackVersion is { } v)
        {
            n ??= Strings.UpdateRolledBack(v, _rt.Current.ToString());
            st.RolledBackVersion = null;   // 一度だけ
            _rt.Store.SaveState(st);
        }
        if (n is not null) _host.ShowNotice(n, null, null, _boot.GiveUp is null ? TimeSpan.FromMinutes(10) : null);
    }

    // -----------------------------------------------------------------------
    // 献立
    // -----------------------------------------------------------------------

    /// <summary>
    /// 献立とタブの「更新」に出す中身。**両方ともここから作る**（片方だけ表示の決まりが変わらないように）。
    /// 読むだけで、確かめにも行かず何も書かない。
    /// </summary>
    internal sealed record MenuModel(
        string Current, string LastCheck, string? Status, string? Blocked,
        UpdateMode Mode, string NotesPage);

    public MenuModel Model()
    {
        var st = _rt.Store.LoadState();
        var mode = Mode;
        string? status = mode == UpdateMode.Off ? null
            : st.Pending is { } p ? Strings.UpdateStaged(p.Version)
            : st.LatestVersion is { } lv && AppVersion.TryParse(lv, out var v) && v > _rt.Current ? Strings.UpdateAvailable(lv)
            : st.LastError is { } err ? Strings.UpdateFailed(err)
            : null;
        return new MenuModel(
            Strings.UpdateCurrent(_rt.Current.ToString()),
            st.LastCheckUtc is { } t ? Strings.UpdateLastCheck(t) : Strings.UpdateNeverChecked,
            status,
            mode == UpdateMode.Auto && !_rt.CanReplace ? Strings.CannotReplace(_rt.Block) : null,
            mode,
            st.LatestPageUrl is { } lp && _rt.Endpoints.IsReleasePage(lp) ? lp : _rt.Endpoints.ReleasesPage);
    }

    /// <summary>
    /// 「PCミエルのお知らせをもう一度見る」。**v0.8.0 では「今すぐ確認」の代わり。通信はしない**（定期の確認も手動の確認も、この版には無い）。
    /// 紹介のページは、案内の窓の「PCミエルの紹介を開く」から開ける。
    /// </summary>
    public void ShowFinalNotice()
    {
        if (!_noticeReady) return;   // 入れ替えの確かめの最中は開かない・既読にしない
        FinalNoticeUi.Show(_host);
    }

    public void OpenNotes() => OpenPage(Model().NotesPage);

    /// <summary>右クリックの献立の「更新」。いまの版・最後の確認・今すぐ確認・設定・更新の内容。</summary>
    public MenuItem BuildMenu()
    {
        var m = Model();
        var root = new MenuItem { Header = Strings.UpdateMenu };

        root.Items.Add(new MenuItem { Header = m.Current, IsEnabled = false });
        root.Items.Add(new MenuItem { Header = m.LastCheck, IsEnabled = false });
        if (m.Status is not null) root.Items.Add(new MenuItem { Header = m.Status, IsEnabled = false });
        if (m.Blocked is not null) root.Items.Add(new MenuItem { Header = m.Blocked, IsEnabled = false });

        var again = new MenuItem { Header = Strings.FinalNoticeShowAgain };
        again.IsEnabled = _noticeReady;
        again.Click += (_, _) => ShowFinalNotice();
        root.Items.Add(again);

        root.Items.Add(new Separator());
        foreach (var (mode, header) in Modes())
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = m.Mode == mode };
            var captured = mode;
            item.Click += (_, _) => SetMode(captured);
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());
        var notes = new MenuItem { Header = Strings.UpdateNotes };
        notes.Click += (_, _) => OpenPage(m.NotesPage);
        root.Items.Add(notes);
        return root;
    }

    /// <summary>更新の設定の選び方。献立とタブで同じ並び・同じ文言。</summary>
    public static IReadOnlyList<(UpdateMode Mode, string Header)> Modes() => new[]
    {
        (UpdateMode.Auto, Strings.UpdateModeAuto),
        (UpdateMode.NotifyOnly, Strings.UpdateModeNotify),
        (UpdateMode.Off, Strings.UpdateModeOff),
    };

    public void SetMode(UpdateMode mode)
    {
        _state.UpdateMode = UpdateModes.Name(mode);
        _host.SaveNow();
        if (mode != UpdateMode.Auto)
        {
            var st = _rt.Store.LoadState();
            if (st.Pending is not null) UpdateApply.Drop(_rt, st);
        }
        if (mode == UpdateMode.Off) _host.HideNotice();
        // v0.8.0 は新しい版を確かめに行かない。「自動で更新する」に戻しても通信は始めない
    }

    // -----------------------------------------------------------------------

    private void ShowWithLink(string text, string? page, TimeSpan? hideAfter = null)
    {
        if (page is not null && _rt.Endpoints.IsReleasePage(page))
            _host.ShowNotice(text, Strings.UpdateWhatsNew, () => OpenPage(page), hideAfter ?? TimeSpan.FromMinutes(30));
        else
            _host.ShowNotice(text, null, null, hideAfter ?? TimeSpan.FromMinutes(30));
    }

    /// <summary>Release のページを既定のブラウザで開く。**この repo のページだけ**（UpdateEndpoints で確かめる）。</summary>
    private void OpenPage(string url)
    {
        if (!_rt.Endpoints.IsReleasePage(url) && url != _rt.Endpoints.ReleasesPage) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }   // 更新の都合で常駐の窓を落とさない
    }
}
