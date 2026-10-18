using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using PartsLife.Checks.Protocol;
using PartsLife.CheckWindow;

namespace PartsLife.Checks.Tests;

/// <summary>
/// この版で出す機能（Features）が、窓・入口・書き出しの全部で揃っているかを確かめる。
/// v0.7.1: 記憶媒体の精密チェックは**出す**（2026-10-09 の実機の付き添い試験のあと）、GPU の負荷テストは**まだ止める**。
/// 定数を逆に倒すと、下の試験のどれかが落ちる（片方だけ倒しても落ちる）。
///
/// 昇格はしない。補助 exe を起こす側は、差し替えた「起こす関数」が呼ばれたかどうかだけを見る。
/// 本物の入口（UAC を出す道）は、試験の隣に補助 exe が**無いこと**を確かめてからでないと呼ばない。
/// </summary>
public class FeatureGateTests
{
    [Fact]
    public void Storage_check_is_on_in_this_build() => Assert.True(Features.StorageCheck);

    [Fact]
    public void Gpu_load_test_is_still_off_in_this_build() => Assert.False(Features.GpuLoadTest);

    /// <summary>この版の切り替えのまま、補助 exe を起こす関数まで届く（起こす関数は差し替え。昇格しない）。</summary>
    [Fact]
    public async Task Helper_launch_path_is_allowed_in_this_build()
    {
        bool looked = false, launched = false;
        var (outcome, detail) = await HelperSession.RunAsync(
            enabled: Features.StorageCheck,
            find: () => { looked = true; return @"C:\fake\PartsLife.Check.exe"; },
            launcher: _ => _ => { launched = true; return null; },
            "smart", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(looked);
        Assert.True(launched);
        Assert.Equal(HelperOutcome.Failed, outcome);
        Assert.Equal("process did not start", detail);
    }

    /// <summary>上の試験が空振りでないこと: 止めた版なら、同じ道で探しもせずに断る（守りそのものは残っている）。</summary>
    [Fact]
    public async Task Same_path_refuses_without_looking_when_disabled()
    {
        bool looked = false, launched = false;
        var (outcome, detail) = await HelperSession.RunAsync(
            enabled: false,
            find: () => { looked = true; return @"C:\fake\PartsLife.Check.exe"; },
            launcher: _ => _ => { launched = true; return null; },
            "smart", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HelperOutcome.Disabled, outcome);
        Assert.Null(detail);
        Assert.False(looked);
        Assert.False(launched);
    }

    /// <summary>
    /// 窓が使う入口そのもの。この版では断らずに補助 exe を探しに行く（止めた版なら Disabled）。
    /// **試験の隣に補助 exe が無いことを先に確かめる**ので、UAC は出ない（Missing で終わる）。
    /// </summary>
    [Fact]
    public async Task Public_entry_looks_for_the_helper_in_this_build()
    {
        Assert.Null(HelperSession.FindHelper());   // 有ると本物の UAC を出してしまうので、ここで止める
        var (outcome, _) = await HelperSession.RunAsync("smart", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HelperOutcome.Missing, outcome);
    }

    [Fact]
    public void Test_recording_is_on_in_this_build() => Assert.True(Features.TestRecording);

    [Fact]
    public void Window_shows_the_system_check_the_load_test_storage_and_test_recording()
    {
        Assert.Equal(new[] { "system", "load", "storage", "record" }, CheckWindowShot.Tabs);
        OnSta(() =>
        {
            var w = new CheckWindow.CheckWindow();
            try
            {
                Assert.Equal(new[] { T.TabSystem, T.TabLoad, T.TabStorage, T.TabRecord }, w.TabTitles);
                Assert.IsType<SystemPanel>(w.Panel(0));
                Assert.IsType<LoadPanel>(w.Panel(1));
                Assert.IsType<StoragePanel>(w.Panel(2));
                Assert.IsType<RecordPanel>(w.Panel(3));
            }
            finally { w.Close(); }
        });
    }

    /// <summary>
    /// 「押したときだけ動く」: 窓を開いて記録の面を出し、閉じるまでの間、何も記録しない・何も書かない
    /// （ドキュメント\PartsLife\runs に増える物が無い）。ボタンを押さなければ、センサーも読み込まない。
    /// </summary>
    [Fact]
    public void Test_recording_face_writes_nothing_until_a_button_is_pressed()
    {
        var before = RunFiles();
        OnSta(() =>
        {
            var w = new CheckWindow.CheckWindow();
            try
            {
                w.Select(3);
                Assert.IsType<RecordPanel>(w.Panel(3));
                Assert.True(w.Panel(3).Running.IsCompleted);
            }
            finally { w.Close(); }
        });
        Assert.Equal(before, RunFiles());
    }

    private static string[] RunFiles()
    {
        string dir = PartsLife.Checks.Recording.RunStore.DefaultFolder();
        return Directory.Exists(dir) ? Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    }

    /// <summary>
    /// --shot-check record は、本当に 6 秒だけ記録して要約と書き出しの見本まで描く。
    /// 記録は一時フォルダに書いて終わったら消し、ドキュメントには何も書かない。
    /// </summary>
    [Fact]
    public async Task Shot_of_the_record_face_records_in_a_temp_folder_and_leaves_documents_alone()
    {
        var before = RunFiles();
        string path = Path.Combine(Path.GetTempPath(), "pl-gate-rec-" + Guid.NewGuid().ToString("N") + ".png");
        string[] tempBefore = Directory.GetDirectories(Path.GetTempPath(), "pl-shot-record-*");
        try
        {
            await OnStaAsync(() => CheckWindowShot.SaveAsync(path, "record", 1, "ja"));
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 5000);
        }
        finally { File.Delete(path); }
        Assert.Equal(before, RunFiles());
        Assert.Equal(tempBefore.Length, Directory.GetDirectories(Path.GetTempPath(), "pl-shot-record-*").Length);   // 一時フォルダは消してある
    }

    [Fact]
    public void Load_panel_still_does_not_offer_the_gpu()
    {
        Assert.Equal(new[] { LoadPanel.Kind.Cpu, LoadPanel.Kind.Memory }, LoadPanel.Kinds);
        OnSta(() =>
        {
            var p = new LoadPanel();
            Assert.Equal(new[] { "CPU", T.Memory }, p.KindTitles);
        });
    }

    /// <summary>
    /// --shot-check storage は描ける。そして**補助 exe を起こす入口に一度も入らない**（UAC を出さない）。
    /// 知らない名前は今までどおり ArgumentException で、何も書かない。
    /// </summary>
    [Fact]
    public async Task Shot_of_the_storage_face_renders_without_starting_the_helper()
    {
        string path = Path.Combine(Path.GetTempPath(), "pl-gate-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            int before = HelperSession.PublicRuns;
            await OnStaAsync(() => CheckWindowShot.SaveAsync(path, "storage", 1, "ja"));
            Assert.Equal(before, HelperSession.PublicRuns);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 1000);
        }
        finally { File.Delete(path); }

        await Assert.ThrowsAsync<ArgumentException>(() => CheckWindowShot.SaveAsync(path, "nope", 1, "ja"));
        Assert.False(File.Exists(path));
    }

    /// <summary>WPF の部品は STA の糸でしか作れない。</summary>
    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "STA thread did not finish");
        error?.Throw();
    }

    /// <summary>
    /// 非同期の WPF の処理を STA の糸で回す。await の続きがその糸に戻るよう、
    /// Dispatcher を同期文脈にして、終わるまでその糸で Dispatcher を回す。
    /// </summary>
    private static Task OnStaAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var frame = new DispatcherFrame();
            Run();
            Dispatcher.PushFrame(frame);

            async void Run()
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { frame.Continue = false; }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
