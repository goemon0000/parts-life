using System.Security.Cryptography;
using System.Text;

namespace PartsLife.Update.Tests;

/// <summary>入れ替え・戻し・後片付けを一時フォルダで回す。</summary>
public class SwapperTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _app, _staged;

    public SwapperTests()
    {
        _app = _tmp.Sub("app");
        _staged = _tmp.Sub("staged");
    }

    public void Dispose() => _tmp.Dispose();

    private string App(string name) => Path.Combine(_app, name);

    private StagedFile Stage(string name, string body)
    {
        File.WriteAllText(Path.Combine(_staged, name), body);
        return new StagedFile { Name = name, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) };
    }

    private void Install(string name, string body) => File.WriteAllText(App(name), body);

    [Fact]
    public void Apply_moves_current_to_old_and_new_into_place()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        Install("PartsLife.Check.exe", "v0.7.2 helper");
        var files = new[] { Stage("PartsLife.exe", "v0.7.3 main"), Stage("PartsLife.Check.exe", "v0.7.3 helper") };

        Assert.True(Swapper.Apply(_app, _staged, files).Ok);
        Assert.Equal("v0.7.3 main", File.ReadAllText(App("PartsLife.exe")));
        Assert.Equal("v0.7.3 helper", File.ReadAllText(App("PartsLife.Check.exe")));
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe.old")));
        Assert.Equal("v0.7.2 helper", File.ReadAllText(App("PartsLife.Check.exe.old")));
        Assert.False(File.Exists(App("PartsLife.exe.new")));
        Assert.True(Swapper.HasOld(_app, ["PartsLife.exe", "PartsLife.Check.exe"]));
    }

    [Fact]
    public void Tampered_staging_is_refused_and_nothing_changes()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        var f = Stage("PartsLife.exe", "v0.7.3 main");
        File.WriteAllText(Path.Combine(_staged, "PartsLife.exe"), "tampered");

        Assert.False(Swapper.Apply(_app, _staged, [f]).Ok);
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));
        Assert.Equal(["PartsLife.exe"], Directory.GetFiles(_app).Select(x => Path.GetFileName(x)!).ToArray());
    }

    [Fact]
    public void Failure_half_way_puts_everything_back()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        Install("PartsLife.Check.exe", "v0.7.2 helper");
        // 2 つ目の .old の名前をフォルダで塞いでおく → 2 つ目の付け替えで失敗する
        Directory.CreateDirectory(App("PartsLife.Check.exe.old"));
        var files = new[] { Stage("PartsLife.exe", "v0.7.3 main"), Stage("PartsLife.Check.exe", "v0.7.3 helper") };

        var why = Swapper.Apply(_app, _staged, files);
        Assert.False(why.Ok);
        Assert.False(why.Dirty);
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));
        Assert.Equal("v0.7.2 helper", File.ReadAllText(App("PartsLife.Check.exe")));
        Assert.False(File.Exists(App("PartsLife.exe.old")));
        Assert.False(File.Exists(App("PartsLife.exe.new")));
        Assert.False(File.Exists(App("PartsLife.Check.exe.new")));
    }

    [Fact]
    public void A_leftover_old_from_last_time_is_replaced()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        Install("PartsLife.exe.old", "v0.7.1 main");
        Assert.True(Swapper.Apply(_app, _staged, [Stage("PartsLife.exe", "v0.7.3 main")]).Ok);
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe.old")));
    }

    [Fact]
    public void Rollback_restores_old_and_cleanup_removes_the_rest()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        Install("PartsLife.Check.exe", "v0.7.2 helper");
        var names = new[] { "PartsLife.exe", "PartsLife.Check.exe" };
        Assert.True(Swapper.Apply(_app, _staged, [Stage("PartsLife.exe", "v0.7.3 main"), Stage("PartsLife.Check.exe", "v0.7.3 helper")]).Ok);

        Assert.Null(Swapper.Rollback(_app, names));
        Assert.Equal("v0.7.2 main", File.ReadAllText(App("PartsLife.exe")));
        Assert.Equal("v0.7.2 helper", File.ReadAllText(App("PartsLife.Check.exe")));
        Assert.False(Swapper.HasOld(_app, names));

        Swapper.Cleanup(_app, names);
        Assert.Equal(["PartsLife.Check.exe", "PartsLife.exe"], Directory.GetFiles(_app).Select(x => Path.GetFileName(x)!).Order().ToArray());
    }

    [Fact]
    public void Cleanup_after_success_keeps_the_new_version()
    {
        Install("PartsLife.exe", "v0.7.2 main");
        Assert.True(Swapper.Apply(_app, _staged, [Stage("PartsLife.exe", "v0.7.3 main")]).Ok);
        Assert.Equal(1, Swapper.Cleanup(_app, ["PartsLife.exe"]));
        Assert.Equal("v0.7.3 main", File.ReadAllText(App("PartsLife.exe")));
        Assert.Equal(["PartsLife.exe"], Directory.GetFiles(_app).Select(x => Path.GetFileName(x)!).ToArray());
    }

    [Fact]
    public void Rollback_without_old_does_nothing()
    {
        Install("PartsLife.exe", "v0.7.3 main");
        Assert.Null(Swapper.Rollback(_app, ["PartsLife.exe"]));
        Assert.Equal("v0.7.3 main", File.ReadAllText(App("PartsLife.exe")));
    }

    [Fact]
    public void Writable_probe_leaves_nothing_behind()
    {
        Assert.True(Swapper.IsWritable(_app));
        Assert.Empty(Directory.GetFiles(_app));
        Assert.False(Swapper.IsWritable(Path.Combine(_app, "does-not-exist")));
    }
}

/// <summary>覚え書きの読み書き。壊れていても空から始める。</summary>
public class StoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Round_trips_and_survives_garbage()
    {
        var store = new UpdateStore(_tmp.Sub("updates"));
        var s = new UpdateState { LastCheckUtc = DateTimeOffset.UnixEpoch, LatestVersion = "0.7.3" };
        s.MarkFailed("0.7.4");
        s.Pending = new PendingUpdate { Version = "0.7.3", Layout = "zip", Files = [new StagedFile { Name = "PartsLife.exe", Sha256 = "ab" }] };
        store.SaveState(s);
        var back = store.LoadState();
        Assert.Equal(DateTimeOffset.UnixEpoch, back.LastCheckUtc);
        Assert.Equal(["0.7.4"], back.FailedVersions);
        Assert.Equal("PartsLife.exe", back.Pending!.Files[0].Name);

        File.WriteAllText(store.StatePath, "{ broken");
        Assert.Null(store.LoadState().LastCheckUtc);

        store.SaveSwap(new SwapRecord { From = "0.7.2", To = "0.7.3", Launches = 1 });
        Assert.Equal(1, store.LoadSwap()!.Launches);
        store.DeleteSwap();
        Assert.Null(store.LoadSwap());
    }

    [Fact]
    public void Deletes_only_staging_for_versions_at_or_below()
    {
        var store = new UpdateStore(_tmp.Sub("updates"));
        foreach (var v in new[] { "0.7.2", "0.7.3", "0.7.4" }) Directory.CreateDirectory(store.FilesDir(v));
        Directory.CreateDirectory(Path.Combine(store.Root, "not-a-version"));
        store.DeleteStagedUpTo(new AppVersion(0, 7, 3));
        Assert.Equal(["0.7.4", "not-a-version"], Directory.GetDirectories(store.Root).Select(x => Path.GetFileName(x)!).Order().ToArray());
    }

    [Fact]
    public void Failed_list_does_not_grow_forever()
    {
        var s = new UpdateState();
        for (int i = 0; i < 50; i++) s.MarkFailed($"0.{i}.0");
        Assert.Equal(20, s.FailedVersions.Count);
        Assert.Contains("0.49.0", s.FailedVersions);
    }
}

/// <summary>署名の読み取り（WinVerifyTrust）。Windows でしか確かめられない。</summary>
public class AuthenticodeTests
{
    [Fact]
    public void Unsigned_signed_and_tampered()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 自分で作った DLL は無署名
        Assert.Equal(SignatureKind.Unsigned, Authenticode.Inspect(typeof(Updater).Assembly.Location).Kind);

        // .NET の実行環境の DLL は Microsoft の署名つき（ファイルに埋め込み）
        string signed = typeof(object).Assembly.Location;
        var info = Authenticode.Inspect(signed);
        Assert.Equal(SignatureKind.Valid, info.Kind);
        Assert.Contains("Microsoft", info.Subject);

        // 1 バイト書き換えると「正しくない」
        string copy = Path.Combine(Path.GetTempPath(), "pl-sig-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            var bytes = File.ReadAllBytes(signed);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);
            Assert.Equal(SignatureKind.Invalid, Authenticode.Inspect(copy).Kind);
        }
        finally { File.Delete(copy); }
    }
}
