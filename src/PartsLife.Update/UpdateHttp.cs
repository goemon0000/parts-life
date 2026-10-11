using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PartsLife.Update;

/// <summary>回数制限（403 / 429）。待つ時刻の手がかりを持つ。</summary>
public sealed class RateLimitedException(TimeSpan? retryAfter, DateTimeOffset? reset)
    : Exception("GitHub の回数制限")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public DateTimeOffset? Reset { get; } = reset;
}

/// <summary>更新を中止した理由（利用者に見せる短い日本語）。</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// 通信の口。**このアプリの通信は全部ここを通る。**
///
///   - 自動の転送はしない。302 の行き先を 1 つずつ <see cref="UpdateEndpoints"/> に照らしてから進む（5 回まで）
///   - 送る頭書きは User-Agent（PartsLife/版）と Accept だけ。Cookie は持たない・送らない
///   - 大きさの上限を超えたら、その場で切る
/// 試験では偽の <see cref="HttpMessageHandler"/> を渡す（本物の通信はしない）。
/// </summary>
public sealed class UpdateHttp : IDisposable
{
    public const int MaxRedirects = 5;

    private readonly HttpClient _client;
    private readonly UpdateEndpoints _ep;
    private readonly string _userAgent;

    public UpdateHttp(HttpMessageHandler handler, UpdateEndpoints endpoints, AppVersion current)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _ep = endpoints;
        _userAgent = "PartsLife/" + current;
    }

    /// <summary>本物の通信に使う口。自動の転送・Cookie・自動の展開を切ってある。</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PreAuthenticate = false,
    };

    public void Dispose() => _client.Dispose();

    /// <summary>最新の Release の JSON。</summary>
    public async Task<string> GetLatestReleaseJsonAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendFollowingAsync(_ep.LatestRelease, "application/vnd.github+json",
                                                  _ep.IsAllowedApi, timeout.Token).ConfigureAwait(false);
        var bytes = await ReadCappedAsync(resp, ReleaseParser.MaxJsonBytes, timeout.Token).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>小さな添付物（SHA256.txt）を丸ごと読む。</summary>
    public async Task<byte[]> GetSmallAssetAsync(ReleaseAsset asset, int maxBytes, CancellationToken ct)
    {
        if (asset.Size > maxBytes) throw new UpdateException($"{asset.Name} が大きすぎる（{asset.Size:N0} バイト）");
        using var timeout = Linked(ct, TimeSpan.FromSeconds(60));
        using var resp = await SendFollowingAsync(asset.DownloadUrl, "application/octet-stream",
                                                  _ep.IsAllowedDownloadHop, timeout.Token).ConfigureAwait(false);
        return await ReadCappedAsync(resp, maxBytes, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 添付物をファイルに落とし、SHA-256（小文字の16進）を返す。
    /// **Release が言う大きさ（size）を 1 バイトでも超えたら切る。** 足りなくても失敗にする。
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken ct)
    {
        if (asset.Size > maxBytes) throw new UpdateException($"{asset.Name} が大きすぎる（{asset.Size:N0} バイト）");
        using var timeout = Linked(ct, TimeSpan.FromMinutes(20));
        using var resp = await SendFollowingAsync(asset.DownloadUrl, "application/octet-stream",
                                                  _ep.IsAllowedDownloadHop, timeout.Token).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength is { } len && len != asset.Size)
            throw new UpdateException($"{asset.Name} の大きさが Release の記載と違う");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
        await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > asset.Size) throw new UpdateException($"{asset.Name} が Release の記載より大きい");
                sha.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
            }
            await dst.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        if (total != asset.Size) throw new UpdateException($"{asset.Name} が途中で切れた");
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    // ---------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendFollowingAsync(Uri url, string accept, Func<Uri, bool> allowed,
                                                               CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            if (!allowed(url)) throw new UpdateException($"許していない行き先 {url.Host}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(_userAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var loc = resp.Headers.Location;
                resp.Dispose();
                if (loc is null) throw new UpdateException("転送先が書かれていない");
                if (hop >= MaxRedirects) throw new UpdateException("転送が多すぎる");
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                continue;
            }
            if (UpdateSchedule.IsRateLimited(resp.StatusCode))
            {
                var ex = new RateLimitedException(RetryAfterOf(resp), ResetOf(resp));
                resp.Dispose();
                throw ex;
            }
            if (!resp.IsSuccessStatusCode)
            {
                resp.Dispose();
                throw new UpdateException($"HTTP {code}");
            }
            return resp;
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > max) throw new UpdateException("応答が大きすぎる");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) throw new UpdateException("応答が大きすぎる");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } at) return at - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>X-RateLimit-Reset（UNIX 秒）。残りが 0 のときだけ見る。</summary>
    private static DateTimeOffset? ResetOf(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) || rem.FirstOrDefault() != "0") return null;
        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var vals)
            && long.TryParse(vals.FirstOrDefault(), out long unix) && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(after);
        return cts;
    }
}
