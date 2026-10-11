namespace PartsLife.Update;

/// <summary>
/// 通信してよい行き先。**ここに無い所へは、転送（302）されても行かない。**
///
///   api.github.com                      … 最新の Release を尋ねる（この 1 つの URL だけ）
///   github.com                          … その Release の添付物（/goemon0000/parts-life/releases/download/タグ/名前 だけ）
///   release-assets.githubusercontent.com … github.com の添付物が転送される先（2026-10 に実際に確かめた先）
///   objects.githubusercontent.com       … 以前の転送先。GitHub が戻すことがあるので残している
///
/// どれも https・既定のポート（443）・利用者名なし、に限る。
/// 転送先を GitHub が変えたら、ここに足さない限り更新は「落とせませんでした」で止まる（勝手に広がらない）。
/// </summary>
public sealed class UpdateEndpoints
{
    public const string Owner = "goemon0000";
    public const string Repo = "parts-life";

    /// <summary>最新の Release（下書き・試験版は GitHub がこの URL では返さない。こちらでも弾く）。</summary>
    public Uri LatestRelease { get; }

    /// <summary>添付物の URL はこの後ろに「タグ/名前」が付いた形だけ。</summary>
    public string DownloadPrefix { get; }

    /// <summary>Release の説明のページ（利用者に見せるリンク）はこの後ろにタグが付いた形だけ。</summary>
    public string ReleasePagePrefix { get; }

    /// <summary>添付物の転送先として許す名前。</summary>
    public IReadOnlyCollection<string> RedirectHosts { get; }

    /// <summary>試験用の行き先（127.0.0.1 の http）か。配布物では作れない（<see cref="FromTestFeedVariable"/>）。</summary>
    public bool IsTestFeed { get; }

    private readonly string _apiHost;

    private UpdateEndpoints(Uri latest, string downloadPrefix, string pagePrefix, string[] redirectHosts, bool test)
    {
        LatestRelease = latest;
        DownloadPrefix = downloadPrefix;
        ReleasePagePrefix = pagePrefix;
        RedirectHosts = redirectHosts;
        IsTestFeed = test;
        _apiHost = latest.IdnHost;
    }

    public static readonly UpdateEndpoints GitHub = new(
        new Uri($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest"),
        $"https://github.com/{Owner}/{Repo}/releases/download/",
        $"https://github.com/{Owner}/{Repo}/releases/tag/",
        ["release-assets.githubusercontent.com", "objects.githubusercontent.com"],
        test: false);

    /// <summary>
    /// 試験用の行き先に差し替える環境変数。**Debug ビルドだけが読む。** 値は <c>http://127.0.0.1:ポート/</c>。
    /// その下の <c>releases/latest</c> を最新の Release として読み、<c>releases/download/タグ/名前</c> から落とす。
    /// 手元で偽の新しい版（v9.9.9）を作って、入れ替え・再起動・後片付けを通しで確かめるため（tools/update-dry-run.py）。
    /// </summary>
    public const string TestFeedVariable = "PARTSLIFE_UPDATE_TEST_FEED";

#if DEBUG
    public const bool TestFeedAllowed = true;
#else
    public const bool TestFeedAllowed = false;
#endif

    /// <summary>Debug ビルドで、環境変数に 127.0.0.1 の URL が入っているときだけ試験用の行き先。ほかは null。</summary>
    public static UpdateEndpoints? FromTestFeedVariable() =>
        FromTestFeed(Environment.GetEnvironmentVariable(TestFeedVariable));

    internal static UpdateEndpoints? FromTestFeed(string? value)
    {
#pragma warning disable CS0162 // Release では到達しない
        if (!TestFeedAllowed || string.IsNullOrEmpty(value)) return null;
        return Loopback(value);
#pragma warning restore CS0162
    }

    /// <summary>127.0.0.1 の http に限った行き先。試験（単体試験・通しの確かめ）で使う。</summary>
    internal static UpdateEndpoints? Loopback(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var b)) return null;
        if (b.Scheme != Uri.UriSchemeHttp || b.Host != "127.0.0.1" || b.UserInfo.Length > 0) return null;
        string root = b.GetLeftPart(UriPartial.Path);
        return new UpdateEndpoints(new Uri(root + "releases/latest"), root + "releases/download/",
                                   root + "releases/tag/", [], test: true);
    }

    // ---------------------------------------------------------------------

    /// <summary>https・既定のポート・利用者名なし（試験用の行き先では 127.0.0.1 の http）。</summary>
    private bool IsPlain(Uri u)
    {
        if (!u.IsAbsoluteUri || u.UserInfo.Length > 0) return false;
        if (IsTestFeed) return u.Scheme == Uri.UriSchemeHttp && u.Host == "127.0.0.1" && u.Port == LatestRelease.Port;
        return u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort;
    }

    private static bool SameHost(Uri u, string host) => string.Equals(u.IdnHost, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>最新の Release を尋ねる URL か（転送された先も、同じ api.github.com の中だけ許す）。</summary>
    public bool IsAllowedApi(Uri u) => IsPlain(u) && SameHost(u, _apiHost);

    /// <summary>その Release の添付物の、最初に叩く URL（github.com の決まった場所）。</summary>
    public Uri AssetUrl(string tag, string name) =>
        new(DownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));

    /// <summary>添付物を落とす途中で通ってよい URL か（最初の github.com と、その転送先）。</summary>
    public bool IsAllowedDownloadHop(Uri u)
    {
        if (!IsPlain(u)) return false;
        if (u.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return true;
        foreach (var h in RedirectHosts) if (SameHost(u, h)) return true;
        return false;
    }

    /// <summary>利用者に開かせてよい Release のページか。</summary>
    public bool IsReleasePage(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && IsPlain(u)
        && u.AbsoluteUri.StartsWith(ReleasePagePrefix, StringComparison.Ordinal);

    /// <summary>Release の一覧のページ（「更新の内容」のリンク。まだ何も見つけていないとき用）。</summary>
    public string ReleasesPage => ReleasePagePrefix[..^"tag/".Length];
}
