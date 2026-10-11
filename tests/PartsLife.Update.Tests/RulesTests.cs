using System.Net;

namespace PartsLife.Update.Tests;

/// <summary>版の比較・Release の読み・SHA256.txt・行き先・確認の間隔・置き方・署名の決まり。どれも純粋な関数。</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("v0.7.3", 0, 7, 3)]
    [InlineData("0.7.3", 0, 7, 3)]
    [InlineData("v10.20.30", 10, 20, 30)]
    public void Parses_three_numbers(string text, int a, int b, int c)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(a, b, c), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2.3+build")]
    [InlineData("v1..3")]
    [InlineData("v-1.2.3")]
    [InlineData("v1.2.x")]
    [InlineData(" v1.2.3")]
    [InlineData("v1234567.0.0")]
    public void Rejects_anything_else(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Compares_numerically_not_as_text()
    {
        Assert.True(new AppVersion(0, 7, 10) > new AppVersion(0, 7, 9));
        Assert.True(new AppVersion(0, 10, 0) > new AppVersion(0, 9, 99));
        Assert.True(new AppVersion(1, 0, 0) > new AppVersion(0, 99, 99));
        Assert.False(new AppVersion(0, 7, 2) > new AppVersion(0, 7, 2));
        Assert.Equal("v0.7.3", new AppVersion(0, 7, 3).Tag);
    }

    [Fact]
    public void Assembly_version_drops_the_fourth_number() =>
        Assert.Equal(new AppVersion(0, 7, 2), AppVersion.From(new Version(0, 7, 2, 0)));
}

public class ReleaseParserTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "release-latest.json"));

    [Fact]
    public void Reads_the_recorded_shape()
    {
        var r = ReleaseParser.Parse(Fixture(), UpdateEndpoints.GitHub, out var why);
        Assert.NotNull(r);
        Assert.Null(why);
        Assert.Equal(new AppVersion(0, 7, 3), r.Version);
        Assert.Equal("https://github.com/goemon0000/parts-life/releases/tag/v0.7.3", r.PageUrl);
        Assert.Equal(["PartsLife.exe", "PartsLife.zip", "SHA256.txt"], r.Assets.Select(a => a.Name).Order().ToArray());
        var zip = r.Find("PartsLife.zip")!;
        Assert.Equal("https://github.com/goemon0000/parts-life/releases/download/v0.7.3/PartsLife.zip", zip.DownloadUrl.AbsoluteUri);
        Assert.Equal(new string('2', 64), zip.Sha256Digest);
        Assert.True(zip.Size > 0);
    }

    [Fact]
    public void Drafts_and_prereleases_are_not_updates()
    {
        Assert.Null(ReleaseParser.Parse(Fixture().Replace("\"draft\": false", "\"draft\": true"), UpdateEndpoints.GitHub, out var a));
        Assert.Contains("下書き", a);
        Assert.Null(ReleaseParser.Parse(Fixture().Replace("\"prerelease\": false", "\"prerelease\": true"), UpdateEndpoints.GitHub, out var b));
        Assert.Contains("試験版", b);
    }

    [Theory]
    [InlineData("\"tag_name\": \"v0.7.3\"", "\"tag_name\": \"v0.7.3-rc1\"")]
    [InlineData("\"tag_name\": \"v0.7.3\"", "\"tag_name\": \"0.7.3\"")]
    [InlineData("\"tag_name\": \"v0.7.3\"", "\"tag_name\": null")]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/parts-life/releases/tag/v0.7.3\"",
                "\"html_url\": \"https://github.com/someone-else/parts-life/releases/tag/v0.7.3\"")]
    public void Odd_tag_or_foreign_page_is_rejected(string from, string to)
    {
        string json = Fixture().Replace(from, to);
        Assert.NotEqual(Fixture(), json);
        Assert.Null(ReleaseParser.Parse(json, UpdateEndpoints.GitHub, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Assets_pointing_elsewhere_or_unfinished_are_dropped()
    {
        string json = Fixture()
            .Replace("https://github.com/goemon0000/parts-life/releases/download/v0.7.3/PartsLife.exe",
                     "https://evil.example/PartsLife.exe")
            .Replace("\"state\": \"uploaded\",\n      \"size\": 62", "\"state\": \"starter\",\n      \"size\": 62");
        var r = ReleaseParser.Parse(json, UpdateEndpoints.GitHub, out _)!;
        Assert.Null(r.Find("PartsLife.exe"));
        Assert.Null(r.Find("PartsLife.zip"));
        Assert.NotNull(r.Find("SHA256.txt"));
    }

    [Fact]
    public void An_asset_url_for_another_tag_is_dropped()
    {
        string json = Fixture().Replace("releases/download/v0.7.3/PartsLife.zip", "releases/download/v0.7.2/PartsLife.zip");
        Assert.Null(ReleaseParser.Parse(json, UpdateEndpoints.GitHub, out _)!.Find("PartsLife.zip"));
    }

    [Fact]
    public void Garbage_is_not_an_update()
    {
        Assert.Null(ReleaseParser.Parse("not json", UpdateEndpoints.GitHub, out _));
        Assert.Null(ReleaseParser.Parse("null", UpdateEndpoints.GitHub, out _));
        Assert.Null(ReleaseParser.Parse("{}", UpdateEndpoints.GitHub, out _));
    }

    [Theory]
    [InlineData("sha256:" + "AB" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd", true)]
    [InlineData("sha512:0123", false)]
    [InlineData("sha256:xyz", false)]
    [InlineData(null, false)]
    public void Digest_is_read_only_when_well_formed(string? digest, bool ok) =>
        Assert.Equal(ok, ReleaseParser.DigestHex(digest) is not null);
}

public class Sha256ListTests
{
    private const string A = "72b90b0088c28f8bfeef3b739bfb263d5288674dd384b3f7c141bcbd6d2178ac";
    private const string B = "2c0f3321e02b1487fb08cf02ebcabd6b32292e9318ec02b3be5f266c4bbe88de";

    [Fact]
    public void Reads_what_release_yml_writes()
    {
        // release.yml の Tee-Object が書く形そのまま（CRLF・2 つの空白）
        var list = Sha256List.Parse($"{A}  PartsLife.zip\r\n{B}  PartsLife.exe\r\n");
        Assert.Equal(A, list.For("PartsLife.zip"));
        Assert.Equal(B, list.For("PartsLife.exe"));
    }

    [Fact]
    public void Tolerates_bom_star_and_upper_case()
    {
        var list = Sha256List.Parse($"﻿{A.ToUpperInvariant()} *PartsLife.zip\n");
        Assert.Equal(A, list.For("PartsLife.zip"));
    }

    [Fact]
    public void Names_must_match_exactly()
    {
        var list = Sha256List.Parse($"{A}  PartsLife.zip\n");
        Assert.Null(list.For("partslife.zip"));
        Assert.Null(list.For("PartsLife.zip "));
        Assert.Null(list.For("PartsLife.exe"));
    }

    [Fact]
    public void Conflicting_lines_disable_that_name()
    {
        var list = Sha256List.Parse($"{A}  PartsLife.zip\n{B}  PartsLife.zip\n{B}  PartsLife.exe\n");
        Assert.Null(list.For("PartsLife.zip"));
        Assert.Equal(B, list.For("PartsLife.exe"));
        // 同じ値の重複は食い違いではない
        Assert.Equal(A, Sha256List.Parse($"{A}  PartsLife.zip\n{A}  PartsLife.zip\n").For("PartsLife.zip"));
    }

    [Theory]
    [InlineData("72b90b00  PartsLife.zip")]
    [InlineData(A + " PartsLife.zip")]     // 空白 1 つ（2 つ目が名前の頭になる形）は読まない
    [InlineData(A + "  ")]
    [InlineData("zz" + "b90b0088c28f8bfeef3b739bfb263d5288674dd384b3f7c141bcbd6d2178ac  PartsLife.zip")]
    public void Malformed_lines_are_ignored(string line) => Assert.Empty(Sha256List.Parse(line).Names);

    [Fact]
    public void Hashes_a_file()
    {
        string p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256List.HashFile(p));
        }
        finally { File.Delete(p); }
    }
}

public class EndpointTests
{
    private static readonly UpdateEndpoints G = UpdateEndpoints.GitHub;

    [Fact]
    public void Api_is_the_one_url()
    {
        Assert.Equal("https://api.github.com/repos/goemon0000/parts-life/releases/latest", G.LatestRelease.AbsoluteUri);
        Assert.True(G.IsAllowedApi(G.LatestRelease));
        Assert.False(G.IsAllowedApi(new Uri("http://api.github.com/repos/goemon0000/parts-life/releases/latest")));
        Assert.False(G.IsAllowedApi(new Uri("https://api.github.com:8443/repos/goemon0000/parts-life/releases/latest")));
        Assert.False(G.IsAllowedApi(new Uri("https://user@api.github.com/repos/goemon0000/parts-life/releases/latest")));
        Assert.False(G.IsAllowedApi(new Uri("https://api.github.com.evil.example/x")));
    }

    [Theory]
    [InlineData("https://github.com/goemon0000/parts-life/releases/download/v0.7.3/PartsLife.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc", true)]
    [InlineData("https://github.com/someone/else/releases/download/v0.7.3/PartsLife.zip", false)]
    [InlineData("https://github.com/goemon0000/parts-life/archive/refs/tags/v0.7.3.zip", false)]
    [InlineData("http://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/goemon0000/parts-life/main/x", false)]
    [InlineData("https://evil.example/github.com/goemon0000/parts-life/releases/download/", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/x", false)]
    [InlineData("https://api.github.com/repos/goemon0000/parts-life/releases/latest", false)]
    public void Download_hops_are_allow_listed(string url, bool ok) =>
        Assert.Equal(ok, G.IsAllowedDownloadHop(new Uri(url)));

    [Fact]
    public void Release_page_must_be_this_repo()
    {
        Assert.True(G.IsReleasePage("https://github.com/goemon0000/parts-life/releases/tag/v0.7.3"));
        Assert.False(G.IsReleasePage("https://github.com/x/parts-life/releases/tag/v0.7.3"));
        Assert.False(G.IsReleasePage("javascript:alert(1)"));
        Assert.False(G.IsReleasePage(null));
    }

    [Fact]
    public void Loopback_feed_is_only_127_0_0_1_over_http()
    {
        Assert.NotNull(UpdateEndpoints.Loopback("http://127.0.0.1:5123/"));
        Assert.Null(UpdateEndpoints.Loopback("http://localhost:5123/"));
        Assert.Null(UpdateEndpoints.Loopback("http://10.0.0.1:5123/"));
        Assert.Null(UpdateEndpoints.Loopback("https://127.0.0.1:5123/"));
        var ep = UpdateEndpoints.Loopback("http://127.0.0.1:5123")!;
        Assert.False(ep.IsAllowedDownloadHop(new Uri("http://127.0.0.1:5999/releases/download/v9.9.9/x")));
        Assert.False(ep.IsAllowedDownloadHop(new Uri("https://release-assets.githubusercontent.com/x")));
    }

    /// <summary>**配布物（Release）では、環境変数で行き先を差し替えられない。** テストは -c Release でも回す。</summary>
    [Fact]
    public void Test_feed_variable_is_honoured_only_in_debug_builds()
    {
        string? before = Environment.GetEnvironmentVariable(UpdateEndpoints.TestFeedVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateEndpoints.TestFeedVariable, "http://127.0.0.1:5123/");
            var ep = UpdateEndpoints.FromTestFeedVariable();
#if DEBUG
            Assert.True(UpdateEndpoints.TestFeedAllowed);
            Assert.NotNull(ep);
            Assert.True(ep.IsTestFeed);
#else
            Assert.False(UpdateEndpoints.TestFeedAllowed);
            Assert.Null(ep);
#endif
        }
        finally { Environment.SetEnvironmentVariable(UpdateEndpoints.TestFeedVariable, before); }
    }
}

public class ScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void At_most_once_a_day()
    {
        var s = new UpdateState();
        Assert.True(UpdateSchedule.IsDue(s, T0));
        s.LastCheckUtc = T0;
        Assert.False(UpdateSchedule.IsDue(s, T0.AddHours(23).AddMinutes(59)));
        Assert.True(UpdateSchedule.IsDue(s, T0.AddHours(24)));
        Assert.Equal(T0.AddHours(24), UpdateSchedule.NextDue(s, T0.AddHours(1)));
    }

    [Fact]
    public void A_clock_set_backwards_does_not_block_checks()
    {
        var s = new UpdateState { LastCheckUtc = T0.AddDays(30) };
        Assert.True(UpdateSchedule.IsDue(s, T0));
    }

    [Fact]
    public void Rate_limit_backs_off_a_day_or_longer_if_told()
    {
        Assert.Equal(T0.AddHours(24), UpdateSchedule.BackoffUntil(T0, null, null));
        Assert.Equal(T0.AddHours(24), UpdateSchedule.BackoffUntil(T0, TimeSpan.FromMinutes(5), null));
        Assert.Equal(T0.AddHours(30), UpdateSchedule.BackoffUntil(T0, TimeSpan.FromHours(30), null));
        Assert.Equal(T0.AddHours(40), UpdateSchedule.BackoffUntil(T0, null, T0.AddHours(40)));
        Assert.Equal(T0.AddDays(7), UpdateSchedule.BackoffUntil(T0, TimeSpan.FromDays(365), null));   // 打ち切り
    }

    [Fact]
    public void Backoff_blocks_even_manual_checks_until_it_ends()
    {
        var s = new UpdateState { LastCheckUtc = T0.AddDays(-3), BackoffUntilUtc = T0.AddHours(5) };
        Assert.False(UpdateSchedule.IsDue(s, T0));
        Assert.False(UpdateSchedule.CanCheckNow(s, T0));
        Assert.True(UpdateSchedule.CanCheckNow(s, T0.AddHours(5)));
        Assert.True(UpdateSchedule.IsDue(s, T0.AddHours(5)));
        Assert.Equal(T0.AddHours(5), UpdateSchedule.NextDue(s, T0));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void Rate_limit_statuses(HttpStatusCode code, bool limited) => Assert.Equal(limited, UpdateSchedule.IsRateLimited(code));
}

public class ModeAndLayoutTests
{
    [Theory]
    [InlineData(null, UpdateMode.Auto)]
    [InlineData("", UpdateMode.Auto)]
    [InlineData("auto", UpdateMode.Auto)]
    [InlineData("notify", UpdateMode.NotifyOnly)]
    [InlineData("off", UpdateMode.Off)]
    [InlineData("weekly", UpdateMode.NotifyOnly)]   // 知らない値で勝手に入れ替えない
    public void Mode_names(string? name, UpdateMode mode) => Assert.Equal(mode, UpdateModes.Parse(name));

    [Fact]
    public void Mode_names_round_trip()
    {
        foreach (var m in Enum.GetValues<UpdateMode>()) Assert.Equal(m, UpdateModes.Parse(UpdateModes.Name(m)));
    }

    [Fact]
    public void Layout_follows_the_helper_exe()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "PartsLife.exe"), "");
        Assert.Equal(InstallLayout.SingleExe, InstallLayouts.Detect(dir.Path));
        Assert.Equal("PartsLife.exe", InstallLayouts.AssetFor(InstallLayout.SingleExe));
        Assert.Equal(["PartsLife.exe"], InstallLayouts.FilesFor(InstallLayout.SingleExe));

        File.WriteAllText(Path.Combine(dir.Path, "PartsLife.Check.exe"), "");
        Assert.Equal(InstallLayout.Zip, InstallLayouts.Detect(dir.Path));
        Assert.Equal("PartsLife.zip", InstallLayouts.AssetFor(InstallLayout.Zip));
        Assert.Equal(["PartsLife.exe", "PartsLife.Check.exe"], InstallLayouts.FilesFor(InstallLayout.Zip));
    }
}

public class SignaturePolicyTests
{
    private static readonly SignatureInfo Unsigned = SignatureInfo.Unsigned;
    private static readonly SignatureInfo Broken = new(SignatureKind.Invalid, null);
    private static SignatureInfo By(string s) => new(SignatureKind.Valid, s);

    [Fact]
    public void Unsigned_running_exe_accepts_unsigned_or_signed_but_not_broken()
    {
        Assert.Null(SignaturePolicy.Check(Unsigned, Unsigned, "PartsLife.exe"));
        Assert.Null(SignaturePolicy.Check(Unsigned, By("CN=SignPath Foundation"), "PartsLife.exe"));
        Assert.NotNull(SignaturePolicy.Check(Unsigned, Broken, "PartsLife.exe"));
    }

    [Fact]
    public void Signed_running_exe_requires_the_same_signer()
    {
        var me = By("CN=SignPath Foundation, O=SignPath Foundation, C=US");
        Assert.Null(SignaturePolicy.Check(me, me, "PartsLife.exe"));
        Assert.NotNull(SignaturePolicy.Check(me, Unsigned, "PartsLife.exe"));
        Assert.NotNull(SignaturePolicy.Check(me, Broken, "PartsLife.exe"));
        Assert.NotNull(SignaturePolicy.Check(me, By("CN=Someone Else"), "PartsLife.exe"));
    }
}

public class SwapBootTests
{
    private static readonly AppVersion V073 = new(0, 7, 3);

    [Fact]
    public void Decides_from_the_record()
    {
        Assert.Equal(BootAction.None, SwapBoot.Decide(null, V073));
        Assert.Equal(BootAction.Verify, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "0.7.3" }, V073));
        Assert.Equal(BootAction.Verify, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "0.7.3", Launches = 1 }, V073));
        Assert.Equal(BootAction.Rollback, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "0.7.3", Launches = 2 }, V073));
        Assert.Equal(BootAction.Cleanup, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "0.7.3", Confirmed = true }, V073));
        // 戻した後の古い版（記録の To と合わない）
        Assert.Equal(BootAction.Stale, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "0.7.3" }, new AppVersion(0, 7, 2)));
        Assert.Equal(BootAction.Stale, SwapBoot.Decide(new SwapRecord { From = "0.7.2", To = "garbage" }, V073));
    }

    [Fact]
    public void Watch_window_covers_the_healthy_mark() =>
        Assert.True(SwapBoot.WatchTimeout > SwapBoot.HealthyAfter + TimeSpan.FromSeconds(60));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pl-update-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
