using System.Security.Cryptography;
using System.Text;

namespace PartsLife.Update.Tests;

/// <summary>swap.json が読めるのに書けない・消せない（読み取り専用・掴まれている）覚え書き。</summary>
internal sealed class ReadOnlySwapStore(string root) : UpdateStore(root)
{
    public int SaveAttempts { get; private set; }
    public override bool SaveSwap(SwapRecord r) { SaveAttempts++; return false; }
    public override bool DeleteSwap() => false;
}

/// <summary>
/// 戻しの決まり（<see cref="SwapRecovery"/>）と、起動のたびの判断（<see cref="SwapBoot.Decide"/>）を合わせて回す。
/// 「起動 → 戻す → 起こし直す → 起動 …」が**必ず止まる**こと、動いていない版を失敗にしないこと、
/// 諦めた後に .old を片付けないこと。
/// </summary>
public class RecoveryTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _app, _staged;
    private static readonly AppVersion V072 = new(0, 7, 2), V073 = new(0, 7, 3);

    public RecoveryTests()
    {
        _app = _tmp.Sub("app");
        _staged = _tmp.Sub("staged");
        File.WriteAllText(App("PartsLife.exe"), "v0.7.2 main");
    }

    public void Dispose() => _tmp.Dispose();

    private string App(string name) => Path.Combine(_app, name);

    private void Swap()
    {
        File.WriteAllText(Path.Combine(_staged, "PartsLife.exe"), "v0.7.3 main");
        var f = new StagedFile { Name = "PartsLife.exe", Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("v0.7.3 main"))) };
        Assert.True(Swapper.Apply(_app, _staged, [f]).Ok);
    }

    /// <summary>launched なら、起こした新しい版がだめで戻すと決めた記録（見届けた側が書く形）。</summary>
    private static SwapRecord Rec(bool launched) =>
        new() { From = "0.7.2", To = "0.7.3", Files = ["PartsLife.exe"], Launched = launched, RollbackRequested = launched };

    /// <summary>
    /// 指摘 1: swap.json が書けず消せず、しかも戻しも失敗するとき、起動のたびに「戻す → 起こし直す」が回り続けないこと。
    /// 試みた回数を残せなければ起こし直さない（Stuck）。
    /// </summary>
    [Fact]
    public void Unwritable_record_with_a_failing_rollback_does_not_loop()
    {
        Swap();
        var store = new ReadOnlySwapStore(_tmp.Sub("updates"));
        var ops = new FaultyOps().Fail("move", (a, _) => Path.GetFileName(a) == "PartsLife.exe.old");
        var rec = Rec(launched: true);
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(rec, V073));

        var r = SwapRecovery.Rollback(store, _app, rec, ops);
        Assert.Equal(RecoveryKind.Stuck, r.Kind);                 // 起こし直さない
        Assert.Equal(1, store.SaveAttempts);
        Assert.True(File.Exists(App("PartsLife.exe")));
        Assert.True(File.Exists(App("PartsLife.exe.old")));
    }

    /// <summary>
    /// 指摘 1: 書けない記録で戻しは成功したとき。起こし直された起動（--update-rolled-back）は、
    /// 残ったままの記録を見ても**戻しをしない**（Hold）。輪が 1 周で止まる。
    /// </summary>
    [Fact]
    public void Relaunch_after_rollback_holds_even_if_the_record_could_not_be_removed()
    {
        Swap();
        var store = new ReadOnlySwapStore(_tmp.Sub("updates"));
        var rec = Rec(launched: true);
        var r = SwapRecovery.Rollback(store, _app, rec);
        Assert.Equal(RecoveryKind.Restored, r.Kind);
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));

        // 記録は消せずに残っている（Launched・RollbackRequested のまま）
        var left = Rec(launched: true);
        left.RollbackRequested = true;
        Assert.Equal(BootAction.Hold, SwapBoot.Decide(left, V072, relaunchedAfterRollback: true));
        Assert.False(SwapBoot.MayCleanup(BootAction.Hold));
        // 普通に起動し直しても、戻す物が無いので起こし直さない
        Assert.Equal(RecoveryKind.NothingToRollBack, SwapRecovery.Rollback(store, _app, left).Kind);
    }

    /// <summary>試みた回数を残せるなら、決めた回数で諦める（GaveUp）。それまでは起こし直して戻し直す。</summary>
    [Fact]
    public void Writable_record_retries_then_gives_up()
    {
        Swap();
        var store = new UpdateStore(_tmp.Sub("updates"));
        var ops = new FaultyOps().Fail("move", (a, _) => Path.GetFileName(a) == "PartsLife.exe.old");
        var rec = Rec(launched: true);
        Assert.True(store.SaveSwap(rec));
        var kinds = new List<RecoveryKind>();
        for (int i = 0; i < 5; i++)
        {
            var now = store.LoadSwap()!;
            var action = SwapBoot.Decide(now, V073);
            if (action != BootAction.Rollback) { Assert.Equal(BootAction.GiveUp, action); break; }
            kinds.Add(SwapRecovery.Rollback(store, _app, now, ops).Kind);
        }
        Assert.Equal([RecoveryKind.RetryRelaunch, RecoveryKind.RetryRelaunch, RecoveryKind.GaveUp], kinds);
        Assert.True(File.Exists(App("PartsLife.exe")));
    }

    /// <summary>
    /// 指摘 2: 名前を変える前に止まった入れ替え（Launched が偽・.old が無い）。その版は動いていないので失敗にせず、
    /// 「うまく起動しなかった」とも言わない。置き場は片付け、次の確認でやり直せる。
    /// </summary>
    [Fact]
    public void Interrupted_before_renaming_is_not_a_failed_version()
    {
        var store = new UpdateStore(_tmp.Sub("updates"));
        Directory.CreateDirectory(store.FilesDir("0.7.3"));
        var rec = Rec(launched: false);
        Assert.True(store.SaveSwap(rec));
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(rec, V072));

        var r = SwapRecovery.Rollback(store, _app, rec);
        Assert.Equal(RecoveryKind.NothingToRollBack, r.Kind);
        Assert.False(r.MarkedFailed);
        var st = store.LoadState();
        Assert.False(st.HasFailed(V073));
        Assert.Null(st.RolledBackVersion);
        Assert.Null(store.LoadSwap());
        Assert.False(Directory.Exists(store.VersionDir("0.7.3")));
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));
    }

    /// <summary>指摘 2: 付け替えの途中で止まった（Launched が偽・.old はある）。戻すが、失敗した版にはしない。</summary>
    [Fact]
    public void Interrupted_mid_swap_is_restored_without_blaming_the_version()
    {
        Swap();
        var store = new UpdateStore(_tmp.Sub("updates"));
        var rec = Rec(launched: false);
        var r = SwapRecovery.Rollback(store, _app, rec);
        Assert.Equal(RecoveryKind.Restored, r.Kind);
        Assert.False(r.MarkedFailed);
        Assert.False(store.LoadState().HasFailed(V073));
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));
    }

    /// <summary>起こした新しい版がだめだったとき（Launched が真）だけ、失敗した版にして知らせる。</summary>
    [Fact]
    public void Launched_and_failed_is_a_failed_version()
    {
        Swap();
        var store = new UpdateStore(_tmp.Sub("updates"));
        var r = SwapRecovery.Rollback(store, _app, Rec(launched: true));
        Assert.Equal(RecoveryKind.Restored, r.Kind);
        Assert.True(r.MarkedFailed);
        Assert.True(store.LoadState().HasFailed(V073));
        Assert.Equal("0.7.3", store.LoadState().RolledBackVersion);
    }

    /// <summary>
    /// 指摘 3: 諦めた（手で .old から戻してもらう）後の起動では、.old を片付けない。記録が残る限りずっと GiveUp。
    /// 記録が消えて（いまの版が無事に動いて）初めて片付けに戻る。
    /// </summary>
    [Fact]
    public void Give_up_keeps_old_until_the_record_is_cleared()
    {
        Swap();
        var rec = Rec(launched: true);
        rec.RollbackRequested = true;
        rec.RollbackAttempts = SwapBoot.MaxRollbackAttempts;
        foreach (var running in new[] { V072, V073 })
        {
            var action = SwapBoot.Decide(rec, running);
            Assert.Equal(BootAction.GiveUp, action);
            Assert.False(SwapBoot.MayCleanup(action));
        }
        // 起こし直された起動でも同じ（Hold にはならない。知らせ続ける）
        Assert.Equal(BootAction.GiveUp, SwapBoot.Decide(rec, V072, relaunchedAfterRollback: true));
        Assert.True(File.Exists(App("PartsLife.exe.old")));
        // 記録が消えたら、普通の片付けに戻る
        Assert.True(SwapBoot.MayCleanup(SwapBoot.Decide(null, V073)));
    }
}
