using System.Security.Cryptography;
using System.Text;

namespace PartsLife.Update.Tests;

/// <summary>決めた操作・決めた場所で IOException を投げる。それ以外は本物の操作。</summary>
internal sealed class FaultyOps : FileOps
{
    private readonly List<(string Op, Func<string, string?, bool> When)> _faults = [];
    public int Thrown { get; private set; }

    public FaultyOps Fail(string op, Func<string, string?, bool> when)
    {
        _faults.Add((op, when));
        return this;
    }

    private void Maybe(string op, string a, string? b = null)
    {
        foreach (var (o, when) in _faults)
            if (o == op && when(a, b)) { Thrown++; throw new IOException($"作り物の失敗: {op} {Path.GetFileName(a)} → {Path.GetFileName(b ?? "")}"); }
    }

    public override void Move(string from, string to) { Maybe("move", from, to); base.Move(from, to); }
    public override void Delete(string path) { Maybe("delete", path); base.Delete(path); }
    public override void Copy(string from, string to) { Maybe("copy", from, to); base.Copy(from, to); }
}

/// <summary>入れ替えと戻しの、途中の 1 か所が失敗したとき。**どの道でも PartsLife.exe が無い状態で終わらない。**</summary>
public class SwapperFaultTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _app, _staged;
    private static readonly string[] Both = ["PartsLife.exe", "PartsLife.Check.exe"];

    public SwapperFaultTests()
    {
        _app = _tmp.Sub("app");
        _staged = _tmp.Sub("staged");
        File.WriteAllText(App("PartsLife.exe"), "v0.7.2 main");
        File.WriteAllText(App("PartsLife.Check.exe"), "v0.7.2 helper");
    }

    public void Dispose() => _tmp.Dispose();

    private string App(string name) => Path.Combine(_app, name);
    private string Read(string name) => File.ReadAllText(App(name));

    private StagedFile[] StageBoth()
    {
        StagedFile One(string name, string body)
        {
            File.WriteAllText(Path.Combine(_staged, name), body);
            return new StagedFile { Name = name, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) };
        }
        return [One("PartsLife.exe", "v0.7.3 main"), One("PartsLife.Check.exe", "v0.7.3 helper")];
    }

    private static bool Is(string path, string name) => Path.GetFileName(path) == name;

    private void AssertUntouched()
    {
        Assert.Equal("v0.7.2 main", Read("PartsLife.exe"));
        Assert.Equal("v0.7.2 helper", Read("PartsLife.Check.exe"));
        Assert.Equal(Both.Order().ToArray(), Directory.GetFiles(_app).Select(x => Path.GetFileName(x)!).Order().ToArray());
    }

    [Fact]
    public void Copy_failure_is_clean()
    {
        var ops = new FaultyOps().Fail("copy", (a, b) => Is(b!, "PartsLife.Check.exe.new"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.False(o.Ok);
        Assert.False(o.Dirty);
        AssertUntouched();
    }

    [Fact]
    public void Leftover_old_that_cannot_be_deleted_is_clean()
    {
        File.WriteAllText(App("PartsLife.exe.old"), "v0.7.1 main");
        var ops = new FaultyOps().Fail("delete", (a, _) => Is(a, "PartsLife.exe.old"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.False(o.Ok);
        Assert.False(o.Dirty);
        Assert.Equal("v0.7.2 main", Read("PartsLife.exe"));
        Assert.Equal("v0.7.1 main", Read("PartsLife.exe.old"));   // 古い控えも失っていない
        Assert.False(File.Exists(App("PartsLife.exe.new")));
    }

    [Fact]
    public void Second_rename_to_old_fails_and_first_is_put_back()
    {
        var ops = new FaultyOps().Fail("move", (a, b) => Is(a, "PartsLife.Check.exe") && Is(b!, "PartsLife.Check.exe.old"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.False(o.Ok);
        Assert.False(o.Dirty);
        AssertUntouched();
    }

    [Fact]
    public void Second_new_into_place_fails_and_both_are_put_back()
    {
        var ops = new FaultyOps().Fail("move", (a, b) => Is(a, "PartsLife.Check.exe.new"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.False(o.Ok);
        Assert.False(o.Dirty);
        AssertUntouched();
    }

    [Fact]
    public void Undo_that_cannot_restore_is_dirty_but_keeps_an_exe_and_the_old()
    {
        // 2 つ目で失敗 → 1 つ目を戻そうとして、.old を元の名前に戻すところも失敗する
        var ops = new FaultyOps()
            .Fail("move", (a, b) => Is(a, "PartsLife.Check.exe.new"))
            .Fail("move", (a, b) => Is(a, "PartsLife.exe.old") && Is(b!, "PartsLife.exe"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.False(o.Ok);
        Assert.True(o.Dirty);                                         // 「戻した」とは言わない
        Assert.Equal("v0.7.3 main", Read("PartsLife.exe"));           // 退けた新しい物を元の名前に戻した
        Assert.Equal("v0.7.2 main", Read("PartsLife.exe.old"));       // .old は残す（次の起動で戻す）
        Assert.Equal("v0.7.2 helper", Read("PartsLife.Check.exe"));
    }

    [Fact]
    public void Undo_where_even_putting_new_back_fails_still_leaves_an_exe()
    {
        var ops = new FaultyOps()
            .Fail("move", (a, b) => Is(a, "PartsLife.Check.exe.new"))
            .Fail("move", (a, b) => Is(a, "PartsLife.exe.old") && Is(b!, "PartsLife.exe"))
            .Fail("move", (a, b) => a.Contains("PartsLife.exe.failed") && Is(b!, "PartsLife.exe"));
        var o = Swapper.Apply(_app, _staged, StageBoth(), ops);
        Assert.True(o.Dirty);
        Assert.True(File.Exists(App("PartsLife.exe")));               // 写しで埋めた
        Assert.Equal("v0.7.3 main", Read("PartsLife.exe"));
        Assert.True(File.Exists(App("PartsLife.exe.old")));
    }

    [Fact]
    public void Rollback_puts_new_back_when_old_cannot_move()
    {
        Assert.True(Swapper.Apply(_app, _staged, StageBoth()).Ok);
        var ops = new FaultyOps().Fail("move", (a, b) => Is(a, "PartsLife.exe.old"));
        var err = Swapper.Rollback(_app, Both, ops);
        Assert.NotNull(err);
        Assert.Equal("v0.7.3 main", Read("PartsLife.exe"));           // 消えていない
        Assert.Equal("v0.7.2 main", Read("PartsLife.exe.old"));       // 次の起動で戻し直せる
        Assert.False(File.Exists(App("PartsLife.exe.failed")));
        // 2 つ目は戻せている
        Assert.Equal("v0.7.2 helper", Read("PartsLife.Check.exe"));

        // 次の起動で（失敗が無ければ）戻し切れる
        Assert.Null(Swapper.Rollback(_app, Both));
        AssertUntouched();
    }

    [Fact]
    public void Rollback_when_moving_new_aside_fails_changes_nothing()
    {
        Assert.True(Swapper.Apply(_app, _staged, StageBoth()).Ok);
        var ops = new FaultyOps().Fail("move", (a, b) => Is(a, "PartsLife.exe") && b!.Contains(".failed"));
        Assert.NotNull(Swapper.Rollback(_app, Both, ops));
        Assert.Equal("v0.7.3 main", Read("PartsLife.exe"));
        Assert.Equal("v0.7.2 main", Read("PartsLife.exe.old"));
    }
}

public class SwapBootRecordTests
{
    private static readonly AppVersion V072 = new(0, 7, 2), V073 = new(0, 7, 3);

    [Fact]
    public void An_interrupted_swap_is_rolled_back_whoever_starts()
    {
        var rec = new SwapRecord { From = "0.7.2", To = "0.7.3", Launched = false };
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(rec, V072));
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(rec, V073));
    }

    [Fact]
    public void A_requested_rollback_is_retried_then_given_up()
    {
        var rec = new SwapRecord { From = "0.7.2", To = "0.7.3", Launched = true, RollbackRequested = true, RollbackAttempts = 1 };
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(rec, V072));
        rec.RollbackAttempts = SwapBoot.MaxRollbackAttempts;
        Assert.Equal(BootAction.GiveUp, SwapBoot.Decide(rec, V072));
    }
}

public class UpdatePlanTests
{
    private static PendingUpdate Zip(params string[] names) => new()
    {
        Version = "0.7.3",
        Layout = "zip",
        Files = names.Select(n => new StagedFile { Name = n, Sha256 = new string('a', 64) }).ToList(),
    };

    [Fact]
    public void Accepts_exactly_the_layouts_files()
    {
        Assert.Null(UpdatePlan.Check(Zip("PartsLife.exe", "PartsLife.Check.exe"), InstallLayout.Zip, new(0, 7, 2), new UpdateState()));
    }

    [Theory]
    [InlineData("PartsLife.exe")]
    [InlineData("PartsLife.exe", "PartsLife.Check.exe", "evil.dll")]
    [InlineData("PartsLife.exe", "..\\PartsLife.Check.exe")]
    [InlineData("PartsLife.Check.exe", "PartsLife.exe")]
    [InlineData("PartsLife.exe", "partslife.check.exe")]
    public void Refuses_other_names(params string[] names) =>
        Assert.NotNull(UpdatePlan.Check(Zip(names), InstallLayout.Zip, new(0, 7, 2), new UpdateState()));

    [Fact]
    public void Refuses_layout_version_failed_or_missing_hash()
    {
        var ok = Zip("PartsLife.exe", "PartsLife.Check.exe");
        Assert.NotNull(UpdatePlan.Check(ok, InstallLayout.SingleExe, new(0, 7, 2), new UpdateState()));
        Assert.NotNull(UpdatePlan.Check(ok, InstallLayout.Zip, new(0, 7, 3), new UpdateState()));
        var failed = new UpdateState();
        failed.MarkFailed("0.7.3");
        Assert.NotNull(UpdatePlan.Check(ok, InstallLayout.Zip, new(0, 7, 2), failed));
        ok.Files[1].Sha256 = "";
        Assert.NotNull(UpdatePlan.Check(ok, InstallLayout.Zip, new(0, 7, 2), new UpdateState()));
    }
}

public class PersistenceAndLockTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Swap_record_that_cannot_be_written_reports_false()
    {
        string root = Path.Combine(_tmp.Path, "updates");
        File.WriteAllText(root, "a file where the folder should be");   // フォルダを作れない
        var store = new UpdateStore(root);
        Assert.False(store.SaveSwap(new SwapRecord { From = "0.7.2", To = "0.7.3" }));
        Assert.False(store.SaveState(new UpdateState()));
    }

    [Fact]
    public void Swap_record_is_verified_by_reading_back()
    {
        var store = new UpdateStore(_tmp.Sub("updates"));
        Assert.True(store.SaveSwap(new SwapRecord { From = "0.7.2", To = "0.7.3", Launched = true }));
        Assert.True(store.LoadSwap()!.Launched);
        Assert.True(store.DeleteSwap());
    }

    [Fact]
    public void Repeated_apply_failures_retire_the_version()
    {
        var s = new UpdateState();
        Assert.False(s.RecordApplyFailure("0.7.3"));
        Assert.False(s.RecordApplyFailure("0.7.3"));
        Assert.True(s.RecordApplyFailure("0.7.3"));
        Assert.True(s.HasFailed(new AppVersion(0, 7, 3)));
        // 版が変われば数え直す
        Assert.False(s.RecordApplyFailure("0.7.4"));
        Assert.Equal(1, s.ApplyFailures);
    }

    [Fact]
    public void Lock_is_exclusive_until_released()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = @"Local\PartsLife.Update.test." + Guid.NewGuid().ToString("N");
        using var mine = new UpdateLock(name);
        Assert.True(mine.TryAcquire(TimeSpan.Zero));
        bool otherGot = true;
        var t = new Thread(() =>
        {
            using var other = new UpdateLock(name);
            otherGot = other.TryAcquire(TimeSpan.FromMilliseconds(200));
        });
        t.Start();
        t.Join();
        Assert.False(otherGot);   // 入れ替えの最中は、別の起動は握れない
        mine.Release();
        var t2 = new Thread(() =>
        {
            using var other = new UpdateLock(name);
            otherGot = other.TryAcquire(TimeSpan.FromMilliseconds(200));
        });
        t2.Start();
        t2.Join();
        Assert.True(otherGot);
    }
}
