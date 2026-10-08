using PartsLife.Checks.Protocol;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 管理者で起動する exe の置き場所。**配布物（Release）では環境変数で差し替えられない**こと。
/// テストは Debug でも Release でも回す（dotnet test -c Release）。
/// </summary>
public class HelperLocationTests : IDisposable
{
    private readonly string _app = Path.Combine(AppContext.BaseDirectory, "helper-location", Guid.NewGuid().ToString("N"), "app");
    private readonly string _elsewhere = Path.Combine(AppContext.BaseDirectory, "helper-location", Guid.NewGuid().ToString("N"), "evil");

    public HelperLocationTests()
    {
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _app, _elsewhere })
            try { Directory.Delete(Path.GetDirectoryName(d)!, recursive: true); } catch (IOException) { }
    }

    private string Plant(string dir)
    {
        string p = Path.Combine(dir, HelperSession.ExeName);
        File.WriteAllText(p, "not a real exe");
        return p;
    }

    [Fact]
    public void Override_is_honoured_only_in_debug_builds()
    {
        string evil = Plant(_elsewhere);
        string? found = HelperSession.FindHelper(_app, evil);
#if DEBUG
        Assert.True(HelperSession.OverrideAllowed);
        Assert.Equal(evil, found);
#else
        Assert.False(HelperSession.OverrideAllowed);
        Assert.Null(found);                       // 本体の隣に無ければ「無い」。環境変数の先は見ない
        string mine = Plant(_app);
        Assert.Equal(mine, HelperSession.FindHelper(_app, evil));
#endif
    }

    [Fact]
    public void Release_ignores_the_real_environment_variable()
    {
        string evil = Plant(_elsewhere);
        string? before = Environment.GetEnvironmentVariable(HelperSession.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(HelperSession.OverrideVariable, evil);
            string? found = HelperSession.FindHelper();
#if DEBUG
            Assert.Equal(evil, found);
#else
            Assert.NotEqual(evil, found);
            if (found is not null) Assert.Equal(Path.Combine(AppContext.BaseDirectory, HelperSession.ExeName), found);
#endif
        }
        finally { Environment.SetEnvironmentVariable(HelperSession.OverrideVariable, before); }
    }

    [Fact]
    public void Helper_is_taken_from_the_app_directory_as_an_absolute_path()
    {
        string mine = Plant(_app);
        string? found = HelperSession.FindHelper(_app, null);
        Assert.Equal(mine, found);
        Assert.True(Path.IsPathFullyQualified(found!));
        Assert.Null(HelperSession.FindHelper(_elsewhere, null));
        // 相対の場所は受け付けない（作業フォルダ次第で別の物を指す）
        Assert.Null(HelperSession.FindHelper("relative" + Path.DirectorySeparatorChar + "app", null));
        // Debug の差し替えでも、相対の場所は無視して本体の隣を使う
        Assert.Equal(mine, HelperSession.FindHelper(_app, "relative\\" + HelperSession.ExeName));
    }
}
