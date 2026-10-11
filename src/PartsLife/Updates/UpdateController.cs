using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PartsLife.Game;
using PartsLife.I18n;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 常駐の窓と更新の繋ぎ。いつ確かめるか・何を知らせるか・いつ入れ替えるか。
///
/// **いつ入れ替えるか。** このアプリは常駐していて、何週間も閉じない人がいる。「次の起動で」だけだと
/// その人には永久に届かない。そこで、落として照合し終えたら、手が空いているときに入れ替える:
///   - 点検の窓が開いていない（負荷テストや表面検査の最中に止めない）
///   - 右クリックの献立・確認の窓が開いていない、窓の上に指が載っていない
/// 入れ替えは、進行を保存してから窓を隠し、新しい版を起こすだけ。窓は同じ場所に数秒で戻る。
/// 手が空かないまま閉じられたら、次の起動で窓を出す前に入れ替える（<see cref="UpdateBoot"/>）。
/// </summary>
internal sealed class UpdateController
{
    private readonly MainWindow _host;
    private readonly PartyState _state;
    private readonly UpdateRuntime _rt;
    private SwapRecord? _justUpdated;

    private readonly DispatcherTimer _first = new();
    private readonly DispatcherTimer _poll = new();
    private readonly DispatcherTimer _idle = new();
    private readonly DispatcherTimer _healthy = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateController(MainWindow host, PartyState state, UpdateRuntime rt, SwapRecord? justUpdated)
    {
        _host = host;
        _state = state;
        _rt = rt;
        _justUpdated = justUpdated;

        // 試験用の行き先（Debug ビルドだけ）では待ちを縮める。配布物では UpdateSchedule の値のまま
        _first.Interval = rt.Endpoints.IsTestFeed ? TimeSpan.FromSeconds(3) : UpdateSchedule.FirstDelay;
        _poll.Interval = UpdateSchedule.Poll;
        _idle.Interval = rt.Endpoints.IsTestFeed ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(1);
        _healthy.Interval = SwapBoot.HealthyAfter;

        _first.Tick += (_, _) => { _first.Stop(); RunCheck(manual: false); };
        _poll.Tick += (_, _) => RunCheck(manual: false);
        _idle.Tick += (_, _) => ApplyWhenIdle();
        _healthy.Tick += (_, _) => { _healthy.Stop(); ConfirmStarted(); };
    }

    private UpdateMode Mode => UpdateModes.Parse(_state.UpdateMode);

    /// <summary>窓が出たら呼ぶ。</summary>
    public void Start()
    {
        if (_justUpdated is not null) _healthy.Start();
        else ShowRollbackNotice();
        _first.Start();
        _poll.Start();
    }

    /// <summary>
    /// 窓が閉じられた（利用者が終了を選んだ）。入れ替えた直後の確かめの最中なら、ここで「起動できた」と書く。
    /// **普通に閉じられたのは落ちたのとは違う。** 書かずに終わると、見張っている古い版が「起動できなかった」と取って元に戻してしまう。
    /// .old は次の起動で消す（<see cref="BootAction.Cleanup"/>）。
    /// </summary>
    public void OnAppClosing()
    {
        if (_justUpdated is null || !_healthy.IsEnabled) return;
        _healthy.Stop();
        _justUpdated = null;
        if (_rt.Store.LoadSwap() is { } rec && rec.To == _rt.Current.ToString())
        {
            rec.Confirmed = true;
            _rt.Store.SaveSwap(rec);
        }
    }

    public void Stop()
    {
        _first.Stop();
        _poll.Stop();
        _idle.Stop();
        _healthy.Stop();
        _cts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // 確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// 確かめに行く。**「確認しない」なら何もしない**（Updater の側でも止まる。二重の歯止め）。
    /// 決まった時刻の確認は 24 時間に 1 回まで（Updater が覚え書きを見て止める）。
    /// </summary>
    private async void RunCheck(bool manual)
    {
        if (_busy || Mode == UpdateMode.Off) return;
        _busy = true;
        var mode = Mode;
        _cts = new CancellationTokenSource();
        if (manual) _host.ShowNotice(Strings.UpdateChecking, null, null);
        CheckResult r;
        try
        {
            var updater = new Updater(_rt.Context());
            var token = _cts.Token;
            r = await Task.Run(() => updater.CheckAsync(mode, manual, token));
        }
        catch (Exception e)
        {
            r = new CheckResult(CheckKind.Failed, Message: e.Message);
        }
        finally
        {
            _busy = false;
        }
        Guard(() => Handle(r, manual));
    }

    private void Handle(CheckResult r, bool manual)
    {
        var mode = Mode;
        if (mode == UpdateMode.Off)
        {
            // 確かめている間に「確認しない」へ切り替えられた。結果は捨てる
            _host.HideNotice();
            return;
        }
        switch (r.Kind)
        {
            case CheckKind.Available:
            {
                var st = _rt.Store.LoadState();
                if (!manual && st.NotifiedVersion == r.Version) break;   // 同じ版で何度も知らせない
                string text = mode == UpdateMode.Auto && !_rt.CanReplace
                    ? Strings.UpdateAvailableBlocked(r.Version!, Strings.CannotReplace(_rt.Block))
                    : Strings.UpdateAvailable(r.Version!);
                ShowWithLink(text, r.PageUrl);
                st.NotifiedVersion = r.Version;
                _rt.Store.SaveState(st);
                break;
            }
            case CheckKind.Staged:
                if (mode != UpdateMode.Auto)
                {
                    UpdateApply.Drop(_rt, _rt.Store.LoadState());
                    break;
                }
                if (manual) ShowWithLink(Strings.UpdateStaged(r.Version!), r.PageUrl);
                _idle.Start();
                break;
            case CheckKind.UpToDate:
                if (manual) _host.ShowNotice(Strings.UpdateUpToDate(_rt.Current.ToString()), null, null, TimeSpan.FromSeconds(20));
                else _host.HideNoticeIfChecking();
                break;
            case CheckKind.SkippedFailed:
                if (manual) ShowWithLink(Strings.UpdateSkipped(r.Version!), r.PageUrl);
                break;
            case CheckKind.RateLimited:
                if (manual) _host.ShowNotice(Strings.UpdateRateLimited, null, null, TimeSpan.FromSeconds(30));
                break;
            case CheckKind.Failed:
                if (manual) _host.ShowNotice(Strings.UpdateFailed(r.Message ?? ""), null, null, TimeSpan.FromSeconds(30));
                break;
        }
    }

    // -----------------------------------------------------------------------
    // 入れ替え
    // -----------------------------------------------------------------------

    private void ApplyWhenIdle()
    {
        if (Mode != UpdateMode.Auto || !_rt.CanReplace) { _idle.Stop(); return; }
        if (!_host.IsIdleForUpdate) return;
        _idle.Stop();

        var st = _rt.Store.LoadState();
        if (st.Pending is not { } p) return;
        _host.SuspendForUpdate();
        var child = UpdateApply.SwapAndRelaunch(_rt, st, p, out var error);
        if (child is null)
        {
            _host.ResumeAfterFailedUpdate();
            _host.ShowNotice(Strings.UpdateSwapFailed(error ?? ""), null, null, TimeSpan.FromMinutes(2));
            return;
        }
        Stop();
        _ = UpdateApply.WatchAsync(_rt, child, () => Application.Current.Shutdown());
    }

    /// <summary>
    /// 入れ替えた新しい版として、窓を出してしばらく無事に動いた。「起動できた」と書き、古い版が終わるのを待って .old を消す。
    /// </summary>
    private void ConfirmStarted()
    {
        var rec = _rt.Store.LoadSwap();
        _justUpdated = null;
        if (rec is null || rec.To != _rt.Current.ToString()) return;
        rec.Confirmed = true;
        _rt.Store.SaveSwap(rec);
        ShowWithLink(Strings.UpdateUpdated(rec.To), rec.PageUrl, TimeSpan.FromMinutes(10));

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

    private void ShowRollbackNotice()
    {
        var st = _rt.Store.LoadState();
        if (st.RolledBackVersion is not { } v) return;
        st.RolledBackVersion = null;
        _rt.Store.SaveState(st);
        _host.ShowNotice(Strings.UpdateRolledBack(v, _rt.Current.ToString()), null, null, TimeSpan.FromMinutes(10));
    }

    // -----------------------------------------------------------------------
    // 献立
    // -----------------------------------------------------------------------

    /// <summary>右クリックの献立の「更新」。いまの版・最後の確認・今すぐ確認・設定・更新の内容。</summary>
    public MenuItem BuildMenu()
    {
        var st = _rt.Store.LoadState();
        var root = new MenuItem { Header = Strings.UpdateMenu };

        root.Items.Add(new MenuItem { Header = Strings.UpdateCurrent(_rt.Current.ToString()), IsEnabled = false });
        root.Items.Add(new MenuItem
        {
            Header = st.LastCheckUtc is { } t ? Strings.UpdateLastCheck(t) : Strings.UpdateNeverChecked,
            IsEnabled = false,
        });
        string? status = Mode == UpdateMode.Off ? null
            : st.Pending is { } p ? Strings.UpdateStaged(p.Version)
            : st.LatestVersion is { } lv && AppVersion.TryParse(lv, out var v) && v > _rt.Current ? Strings.UpdateAvailable(lv)
            : st.LastError is { } err ? Strings.UpdateFailed(err)
            : null;
        if (status is not null) root.Items.Add(new MenuItem { Header = status, IsEnabled = false });
        if (Mode == UpdateMode.Auto && !_rt.CanReplace)
            root.Items.Add(new MenuItem { Header = Strings.CannotReplace(_rt.Block), IsEnabled = false });

        var now = new MenuItem { Header = Strings.UpdateCheckNow };
        if (Mode == UpdateMode.Off)
        {
            // **「確認しない」の間は押せない。** 押せば通信することになり、「確認しない」と食い違う
            now.IsEnabled = false;
            now.ToolTip = Strings.UpdateCheckOffTip;
            ToolTipService.SetShowOnDisabled(now, true);
        }
        else if (_busy || !UpdateSchedule.CanCheckNow(st, DateTimeOffset.UtcNow)) now.IsEnabled = false;
        now.Click += (_, _) => RunCheck(manual: true);
        root.Items.Add(now);

        root.Items.Add(new Separator());
        foreach (var (mode, header) in new[]
                 {
                     (UpdateMode.Auto, Strings.UpdateModeAuto),
                     (UpdateMode.NotifyOnly, Strings.UpdateModeNotify),
                     (UpdateMode.Off, Strings.UpdateModeOff),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = Mode == mode };
            var captured = mode;
            item.Click += (_, _) => SetMode(captured);
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());
        var notes = new MenuItem { Header = Strings.UpdateNotes };
        string page = st.LatestPageUrl is { } lp && _rt.Endpoints.IsReleasePage(lp) ? lp : _rt.Endpoints.ReleasesPage;
        notes.Click += (_, _) => OpenPage(page);
        root.Items.Add(notes);
        return root;
    }

    private void SetMode(UpdateMode mode)
    {
        var before = Mode;
        _state.UpdateMode = UpdateModes.Name(mode);
        _host.SaveNow();
        if (mode != UpdateMode.Auto)
        {
            _cts?.Cancel();
            _idle.Stop();
            var st = _rt.Store.LoadState();
            if (st.Pending is not null) UpdateApply.Drop(_rt, st);
        }
        if (mode == UpdateMode.Off) { _host.HideNotice(); return; }
        // 「自動で更新する」に戻したとき、もう新しい版を知っていれば、待たずに用意を始める（押した人の操作として）
        if (mode == UpdateMode.Auto && before != UpdateMode.Auto)
        {
            var st = _rt.Store.LoadState();
            if (st.LatestVersion is { } lv && AppVersion.TryParse(lv, out var v) && v > _rt.Current) RunCheck(manual: true);
        }
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
