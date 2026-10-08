using System.Runtime.ExceptionServices;
using PartsLife.Checks.Protocol;
using PartsLife.CheckWindow;

namespace PartsLife.Checks.Tests;

/// <summary>
/// v0.7.0 は「記憶媒体の精密チェック」と「GPU の負荷テスト」を止めて出す（Features）。
/// **止めたつもりで、どこかの道が残っていないか**を確かめる。v0.7.1 で定数を true にしたら、
/// 上の2つの試験（定数が false であること）を書き換える。
///
/// 昇格はしない。補助 exe を起こす側は、差し替えた「起こす関数」が呼ばれたかどうかだけを見る。
/// </summary>
public class FeatureGateTests
{
    [Fact]
    public void Storage_check_is_off_in_this_build() => Assert.False(Features.StorageCheck);

    [Fact]
    public void Gpu_load_test_is_off_in_this_build() => Assert.False(Features.GpuLoadTest);

    [Fact]
    public async Task Helper_is_not_even_looked_for_when_the_storage_check_is_off()
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

    /// <summary>上の試験が空振りでないこと: 有効なら、同じ道で「起こす関数」まで届く。</summary>
    [Fact]
    public async Task Same_path_does_reach_the_launcher_when_enabled()
    {
        bool launched = false;
        var (outcome, detail) = await HelperSession.RunAsync(
            enabled: true,
            find: () => @"C:\fake\PartsLife.Check.exe",
            launcher: _ => _ => { launched = true; return null; },
            "smart", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(launched);
        Assert.Equal(HelperOutcome.Failed, outcome);
        Assert.Equal("process did not start", detail);
    }

    /// <summary>窓が使う入口そのもの。この版では探しもせずに断る（試験の隣に補助 exe が無くても Missing ではなく Disabled）。</summary>
    [Fact]
    public async Task Public_entry_refuses_in_this_build()
    {
        var (outcome, _) = await HelperSession.RunAsync("smart", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HelperOutcome.Disabled, outcome);
    }

    [Fact]
    public void Window_shows_only_the_system_check_and_the_load_test()
    {
        Assert.Equal(new[] { "system", "load" }, CheckWindowShot.Tabs);
        OnSta(() =>
        {
            var w = new CheckWindow.CheckWindow();
            try
            {
                Assert.Equal(new[] { T.TabSystem, T.TabLoad }, w.TabTitles);
                Assert.IsType<SystemPanel>(w.Panel(0));
                Assert.IsType<LoadPanel>(w.Panel(1));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Load_panel_does_not_offer_the_gpu()
    {
        Assert.DoesNotContain(LoadPanel.Kind.Gpu, LoadPanel.Kinds);
        OnSta(() =>
        {
            var p = new LoadPanel();
            Assert.Equal(new[] { "CPU", T.Memory }, p.KindTitles);
        });
    }

    [Fact]
    public async Task Shot_of_the_storage_face_is_refused_without_writing_anything()
    {
        string path = Path.Combine(Path.GetTempPath(), "pl-gate-" + Guid.NewGuid().ToString("N") + ".png");
        var disabled = await Assert.ThrowsAsync<FeatureDisabledException>(() => CheckWindowShot.SaveAsync(path, "storage", 1, "ja"));
        Assert.Equal("storage", disabled.Tab);
        Assert.False(File.Exists(path));
        // 知らない名前は今までどおり ArgumentException（止めてある面と区別する）
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
}
