using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PartsLife.Update.Tests;

/// <summary>
/// 偽の GitHub。届いた要求を全部覚えておき、URL ごとに決めた応答を返す。**本物の通信はしない。**
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        string url = request.RequestUri!.AbsoluteUri;
        var resp = Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
        resp.RequestMessage = request;
        return Task.FromResult(resp);
    }

    public void Bytes(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    public void Redirect(string url, string to) => Routes[url] = () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    };
}

/// <summary>偽の Release を組み立てる。中身・SHA256.txt・API の JSON を揃えて、偽の GitHub に載せる。</summary>
internal sealed class FakeRelease
{
    public const string Api = "https://api.github.com/repos/goemon0000/parts-life/releases/latest";
    public const string Dl = "https://github.com/goemon0000/parts-life/releases/download/";

    public string Tag { get; set; } = "v0.7.3";
    public bool Draft { get; set; }
    public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
    /// <summary>SHA256.txt の中身を差し替える（null なら Assets から正しく作る）。</summary>
    public string? ShaText { get; set; }
    /// <summary>API に書く size を差し替える。</summary>
    public Dictionary<string, long> SizeOverride { get; } = new(StringComparer.Ordinal);

    public static byte[] ExeBytes(string marker) => Encoding.UTF8.GetBytes("MZ fake exe " + marker + new string('.', 4000));

    public static byte[] Zip(params (string Name, byte[] Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(body);
            }
        return ms.ToArray();
    }

    public static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public string Json()
    {
        var sha = ShaText ?? string.Join("\r\n", Assets.Select(kv => $"{Hex(kv.Value)}  {kv.Key}")) + "\r\n";
        var all = new Dictionary<string, byte[]>(Assets) { ["SHA256.txt"] = Encoding.UTF8.GetBytes(sha) };
        var assets = all.Select(kv =>
            $$"""{"name":"{{kv.Key}}","state":"uploaded","size":{{SizeOverride.GetValueOrDefault(kv.Key, kv.Value.Length)}},"browser_download_url":"{{Dl}}{{Tag}}/{{kv.Key}}","digest":null}""");
        return $$"""
            {"tag_name":"{{Tag}}","html_url":"https://github.com/goemon0000/parts-life/releases/tag/{{Tag}}",
             "draft":{{(Draft ? "true" : "false")}},"prerelease":false,"assets":[{{string.Join(",", assets)}}]}
            """;
    }

    public void PublishTo(FakeGitHub gh)
    {
        gh.Bytes(Api, Encoding.UTF8.GetBytes(Json()));
        var sha = ShaText ?? string.Join("\r\n", Assets.Select(kv => $"{Hex(kv.Value)}  {kv.Key}")) + "\r\n";
        gh.Bytes(Dl + Tag + "/SHA256.txt", Encoding.UTF8.GetBytes(sha));
        foreach (var (name, body) in Assets) gh.Bytes(Dl + Tag + "/" + name, body);
    }
}

public class UpdaterTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _app;
    private readonly UpdateStore _store;
    private readonly FakeGitHub _gh = new();
    private int _handlersMade;
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private SignatureInfo _runningSig = SignatureInfo.Unsigned;
    private Func<string, SignatureInfo> _candidateSig = _ => SignatureInfo.Unsigned;
    private AppVersion? _fileVersion = new AppVersion(0, 7, 3);
    private AppVersion? _installedVersion = new AppVersion(0, 7, 2);
    private Func<string, ExeIdentity> _identity = _ => new ExeIdentity("パーツぐらし / Parts Life", "PartsLife.dll", "web-autoai.com");

    public UpdaterTests()
    {
        _app = _tmp.Sub("app");
        _store = new UpdateStore(_tmp.Sub("updates"));
        File.WriteAllBytes(Path.Combine(_app, "PartsLife.exe"), FakeRelease.ExeBytes("0.7.2"));
    }

    public void Dispose() => _tmp.Dispose();

    private sealed class FixedTime(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    private Updater Make(string? cannotReplace = null) => new(new UpdateContext
    {
        AppDir = _app,
        Current = new AppVersion(0, 7, 2),
        Store = _store,
        HandlerFactory = () => { _handlersMade++; return _gh; },
        CannotReplaceReason = cannotReplace,
        InspectSignature = p => p == Path.Combine(_app, "PartsLife.exe") ? _runningSig : _candidateSig(p),
        ReadFileVersion = p => p == Path.Combine(_app, "PartsLife.exe") ? _installedVersion : _fileVersion,
        ReadIdentity = p => _identity(p),
        Time = new FixedTime(() => _now),
    });

    private Task<CheckResult> Check(UpdateMode mode, bool manual = false) => Make().CheckAsync(mode, manual, CancellationToken.None);

    private FakeRelease SingleExeRelease(string marker = "0.7.3")
    {
        var r = new FakeRelease();
        r.Assets["PartsLife.exe"] = FakeRelease.ExeBytes(marker);
        r.Assets["PartsLife.zip"] = FakeRelease.Zip(("PartsLife.exe", FakeRelease.ExeBytes("slim")), ("PartsLife.Check.exe", FakeRelease.ExeBytes("helper")));
        return r;
    }

    // ---- 「確認しない」 ----------------------------------------------------

    [Fact]
    public async Task Off_makes_no_network_call_at_all()
    {
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Off, manual: true);
        Assert.Equal(CheckKind.Off, r.Kind);
        Assert.Equal(0, _handlersMade);            // 通信の口を作りもしない
        Assert.Empty(_gh.Requests);
        Assert.False(File.Exists(_store.StatePath)); // 覚え書きも書かない
    }

    [Fact]
    public async Task Not_due_makes_no_network_call()
    {
        _store.SaveState(new UpdateState { LastCheckUtc = _now.AddHours(-3) });
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.NotDue, r.Kind);
        Assert.Equal(0, _handlersMade);
        Assert.Empty(_gh.Requests);
    }

    // ---- 確認 --------------------------------------------------------------

    [Fact]
    public async Task Sends_only_user_agent_and_accept()
    {
        var rel = SingleExeRelease();
        rel.Tag = "v0.7.2";
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.UpToDate, r.Kind);
        var req = Assert.Single(_gh.Requests);
        Assert.Equal(FakeRelease.Api, req.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal(["Accept", "User-Agent"], req.Headers.Select(h => h.Key).Order().ToArray());
        Assert.Equal("PartsLife/0.7.2", req.Headers.UserAgent.ToString());
        Assert.Null(req.Content);
        Assert.Equal(_now, _store.LoadState().LastCheckUtc);
    }

    [Fact]
    public async Task Older_release_is_never_offered()
    {
        var rel = SingleExeRelease();
        rel.Tag = "v0.7.1";
        rel.PublishTo(_gh);
        Assert.Equal(CheckKind.UpToDate, (await Check(UpdateMode.Auto)).Kind);
        Assert.Null(_store.LoadState().Pending);
    }

    [Fact]
    public async Task Notify_only_downloads_nothing()
    {
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.NotifyOnly);
        Assert.Equal(CheckKind.Available, r.Kind);
        Assert.Equal("0.7.3", r.Version);
        Assert.Equal("https://github.com/goemon0000/parts-life/releases/tag/v0.7.3", r.PageUrl);
        Assert.Single(_gh.Requests);
        Assert.Equal("0.7.3", _store.LoadState().LatestVersion);
    }

    [Fact]
    public async Task Unwritable_folder_falls_back_to_notify()
    {
        SingleExeRelease().PublishTo(_gh);
        var r = await Make("書き込めないフォルダ").CheckAsync(UpdateMode.Auto, false, CancellationToken.None);
        Assert.Equal(CheckKind.Available, r.Kind);
        Assert.Equal("書き込めないフォルダ", r.Message);
        Assert.Single(_gh.Requests);
    }

    [Fact]
    public async Task Draft_is_not_an_update()
    {
        var rel = SingleExeRelease();
        rel.Draft = true;
        rel.PublishTo(_gh);
        Assert.Equal(CheckKind.Failed, (await Check(UpdateMode.Auto)).Kind);
        Assert.Single(_gh.Requests);
    }

    // ---- 落として照合 ------------------------------------------------------

    [Fact]
    public async Task Auto_stages_the_single_exe_after_checking_sha256()
    {
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Staged, r.Kind);
        var p = _store.LoadState().Pending!;
        Assert.Equal("0.7.3", p.Version);
        Assert.Equal("exe", p.Layout);
        var f = Assert.Single(p.Files);
        Assert.Equal("PartsLife.exe", f.Name);
        Assert.Equal(FakeRelease.Hex(rel.Assets["PartsLife.exe"]), f.Sha256);
        Assert.Equal(rel.Assets["PartsLife.exe"], File.ReadAllBytes(Path.Combine(_store.FilesDir("0.7.3"), "PartsLife.exe")));
        // 落としたのは SHA256.txt と exe だけ（ZIP は落とさない）
        Assert.Equal([FakeRelease.Api, FakeRelease.Dl + "v0.7.3/SHA256.txt", FakeRelease.Dl + "v0.7.3/PartsLife.exe"],
                     _gh.Requests.Select(q => q.RequestUri!.AbsoluteUri).ToArray());
        Assert.True(Make().PendingIntact(p));

        // もう一度確かめても落とし直さない
        _gh.Requests.Clear();
        Assert.Equal(CheckKind.Staged, (await Check(UpdateMode.Auto, manual: true)).Kind);
        Assert.Single(_gh.Requests);
    }

    [Fact]
    public async Task Zip_layout_downloads_the_zip_and_extracts_both_exes()
    {
        File.WriteAllBytes(Path.Combine(_app, "PartsLife.Check.exe"), FakeRelease.ExeBytes("helper-0.7.2"));
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        Assert.Equal(CheckKind.Staged, (await Check(UpdateMode.Auto)).Kind);
        var p = _store.LoadState().Pending!;
        Assert.Equal("zip", p.Layout);
        Assert.Equal(["PartsLife.exe", "PartsLife.Check.exe"], p.Files.Select(f => f.Name).ToArray());
        Assert.Equal(FakeRelease.ExeBytes("slim"), File.ReadAllBytes(Path.Combine(_store.FilesDir("0.7.3"), "PartsLife.exe")));
        Assert.Contains(_gh.Requests, q => q.RequestUri!.AbsoluteUri.EndsWith("/PartsLife.zip"));
        Assert.DoesNotContain(_gh.Requests, q => q.RequestUri!.AbsoluteUri.EndsWith("/PartsLife.exe"));
        Assert.False(File.Exists(Path.Combine(_store.VersionDir("0.7.3"), "PartsLife.zip.part")));
    }

    [Fact]
    public async Task Hash_mismatch_stages_nothing()
    {
        var rel = SingleExeRelease();
        rel.ShaText = $"{new string('0', 64)}  PartsLife.exe\r\n";
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("SHA-256", r.Message);
        Assert.Null(_store.LoadState().Pending);
        Assert.False(Directory.Exists(_store.VersionDir("0.7.3")));
        Assert.Equal(r.Message, _store.LoadState().LastError);
    }

    [Fact]
    public async Task Missing_sha_line_stages_nothing()
    {
        var rel = SingleExeRelease();
        rel.ShaText = $"{FakeRelease.Hex(rel.Assets["PartsLife.zip"])}  PartsLife.zip\r\n";
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("PartsLife.exe の行が無い", r.Message);
        Assert.DoesNotContain(_gh.Requests, q => q.RequestUri!.AbsoluteUri.EndsWith("/PartsLife.exe"));
    }

    [Fact]
    public async Task Body_longer_than_the_declared_size_is_cut()
    {
        var rel = SingleExeRelease();
        rel.SizeOverride["PartsLife.exe"] = rel.Assets["PartsLife.exe"].Length - 10;
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Null(_store.LoadState().Pending);
    }

    [Fact]
    public async Task Oversized_release_is_refused_before_downloading()
    {
        var rel = SingleExeRelease();
        rel.SizeOverride["PartsLife.exe"] = Updater.MaxDownloadBytes + 1;
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("大きすぎる", r.Message);
        Assert.DoesNotContain(_gh.Requests, q => q.RequestUri!.AbsoluteUri.EndsWith("/PartsLife.exe"));
    }

    [Fact]
    public async Task Zip_with_unexpected_contents_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_app, "PartsLife.Check.exe"), FakeRelease.ExeBytes("helper-0.7.2"));
        var rel = SingleExeRelease();
        rel.Assets["PartsLife.zip"] = FakeRelease.Zip(("PartsLife.exe", FakeRelease.ExeBytes("slim")),
                                                      ("PartsLife.Check.exe", FakeRelease.ExeBytes("helper")),
                                                      ("evil.dll", [1, 2, 3]));
        rel.PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("ZIP の中身", r.Message);
        Assert.False(Directory.Exists(_store.VersionDir("0.7.3")));
    }

    [Fact]
    public async Task Exe_whose_version_differs_from_the_tag_is_refused()
    {
        _fileVersion = new AppVersion(0, 7, 1);
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("版", r.Message);
        Assert.Null(_store.LoadState().Pending);
    }

    [Fact]
    public async Task Signed_running_exe_refuses_an_unsigned_update()
    {
        _runningSig = new SignatureInfo(SignatureKind.Valid, "CN=SignPath Foundation");
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("署名", r.Message);

        _candidateSig = _ => new SignatureInfo(SignatureKind.Valid, "CN=SignPath Foundation");
        Assert.Equal(CheckKind.Staged, (await Check(UpdateMode.Auto, manual: true)).Kind);
    }

    [Fact]
    public async Task Running_exe_with_unverifiable_signature_refuses_any_update()
    {
        _runningSig = new SignatureInfo(SignatureKind.Invalid, null);
        foreach (var cand in new[] { SignatureInfo.Unsigned, new SignatureInfo(SignatureKind.Invalid, null),
                                     new SignatureInfo(SignatureKind.Valid, "CN=SignPath Foundation") })
        {
            _candidateSig = _ => cand;
            SingleExeRelease().PublishTo(_gh);
            var r = await Check(UpdateMode.Auto, manual: true);
            Assert.Equal(CheckKind.Failed, r.Kind);
            Assert.Contains(SignaturePolicy.Unverifiable, r.Message);
            Assert.Null(_store.LoadState().Pending);
        }
    }

    [Fact]
    public async Task Shared_signer_with_another_products_name_is_refused()
    {
        _runningSig = new SignatureInfo(SignatureKind.Valid, "CN=SignPath Foundation");
        _candidateSig = _ => new SignatureInfo(SignatureKind.Valid, "CN=SignPath Foundation");
        _identity = p => p.StartsWith(_app)
            ? new ExeIdentity("パーツぐらし / Parts Life", "PartsLife.dll", "web-autoai.com")
            : new ExeIdentity("Another OSS", "Another.dll", "someone");
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("製品名", r.Message);
    }

    /// <summary>
    /// ほかのプロセスが入れ替え済みで、この古いプロセスが .old から動いているとき。
    /// **落とさない**（落としても入れ替えられず、毎日落として毎日失敗する輪になる）。
    /// </summary>
    [Fact]
    public async Task Already_installed_by_another_process_downloads_nothing()
    {
        _installedVersion = new AppVersion(0, 7, 3);
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.AlreadyInstalled, r.Kind);
        Assert.Single(_gh.Requests);
        Assert.Null(_store.LoadState().Pending);
    }

    [Fact]
    public async Task Previously_failed_version_is_skipped()
    {
        var s = new UpdateState();
        s.MarkFailed("0.7.3");
        _store.SaveState(s);
        SingleExeRelease().PublishTo(_gh);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.SkippedFailed, r.Kind);
        Assert.Single(_gh.Requests);
    }

    // ---- 転送と行き先 ------------------------------------------------------

    [Fact]
    public async Task Follows_the_redirect_to_github_release_assets()
    {
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        const string cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x";
        _gh.Bytes(cdn, rel.Assets["PartsLife.exe"]);
        _gh.Redirect(FakeRelease.Dl + "v0.7.3/PartsLife.exe", cdn);
        Assert.Equal(CheckKind.Staged, (await Check(UpdateMode.Auto)).Kind);
        Assert.Contains(_gh.Requests, q => q.RequestUri!.AbsoluteUri == cdn);
    }

    [Fact]
    public async Task Never_follows_a_redirect_off_the_allow_list()
    {
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        _gh.Bytes("https://evil.example/PartsLife.exe", rel.Assets["PartsLife.exe"]);
        _gh.Redirect(FakeRelease.Dl + "v0.7.3/PartsLife.exe", "https://evil.example/PartsLife.exe");
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.Contains("evil.example", r.Message);
        Assert.DoesNotContain(_gh.Requests, q => q.RequestUri!.Host == "evil.example");   // 行っていない
    }

    [Fact]
    public async Task Never_follows_a_downgrade_to_http()
    {
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        _gh.Redirect(FakeRelease.Dl + "v0.7.3/PartsLife.exe", "http://release-assets.githubusercontent.com/x");
        Assert.Equal(CheckKind.Failed, (await Check(UpdateMode.Auto)).Kind);
        Assert.DoesNotContain(_gh.Requests, q => q.RequestUri!.Scheme == "http");
    }

    [Fact]
    public async Task Redirect_loops_stop()
    {
        var rel = SingleExeRelease();
        rel.PublishTo(_gh);
        const string a = "https://release-assets.githubusercontent.com/a", b = "https://release-assets.githubusercontent.com/b";
        _gh.Redirect(FakeRelease.Dl + "v0.7.3/PartsLife.exe", a);
        _gh.Redirect(a, b);
        _gh.Redirect(b, a);
        var r = await Check(UpdateMode.Auto);
        Assert.Equal(CheckKind.Failed, r.Kind);
        Assert.True(_gh.Requests.Count <= 3 + UpdateHttp.MaxRedirects);
    }

    // ---- 回数制限 ----------------------------------------------------------

    [Fact]
    public async Task Rate_limit_backs_off_for_a_day_and_blocks_manual_checks()
    {
        _gh.Routes[FakeRelease.Api] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset", _now.AddHours(1).ToUnixTimeSeconds().ToString());
            return r;
        };
        Assert.Equal(CheckKind.RateLimited, (await Check(UpdateMode.Auto)).Kind);
        var s = _store.LoadState();
        Assert.Equal(_now.AddHours(24), s.BackoffUntilUtc);

        _gh.Requests.Clear();
        _now = _now.AddHours(23);
        Assert.Equal(CheckKind.RateLimited, (await Check(UpdateMode.Auto, manual: true)).Kind);
        Assert.Empty(_gh.Requests);

        _now = _now.AddHours(1);
        SingleExeRelease().PublishTo(_gh);
        Assert.Equal(CheckKind.Staged, (await Check(UpdateMode.Auto)).Kind);
    }

    [Fact]
    public async Task Retry_after_longer_than_a_day_is_respected()
    {
        _gh.Routes[FakeRelease.Api] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(36));
            return r;
        };
        Assert.Equal(CheckKind.RateLimited, (await Check(UpdateMode.Auto)).Kind);
        Assert.Equal(_now.AddHours(36), _store.LoadState().BackoffUntilUtc);
    }

    [Fact]
    public async Task Failure_still_counts_as_todays_check()
    {
        _gh.Routes[FakeRelease.Api] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(CheckKind.Failed, (await Check(UpdateMode.Auto)).Kind);
        _gh.Requests.Clear();
        _now = _now.AddHours(2);
        Assert.Equal(CheckKind.NotDue, (await Check(UpdateMode.Auto)).Kind);
        Assert.Empty(_gh.Requests);
    }
}
