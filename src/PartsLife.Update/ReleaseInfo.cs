using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartsLife.Update;

/// <summary>Release の添付物 1 つ。URL は <see cref="UpdateEndpoints.AssetUrl"/> と一致した物だけが残る。</summary>
public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl, string? Sha256Digest);

/// <summary>最新の Release。<see cref="ReleaseParser"/> が確かめた物だけがこの形になる。</summary>
public sealed record ReleaseInfo(AppVersion Version, string Tag, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Find(string name)
    {
        foreach (var a in Assets) if (a.Name == name) return a;
        return null;
    }
}

/// <summary>
/// GitHub の <c>releases/latest</c> の応答を読む。使う欄は tag_name・html_url・draft・prerelease・assets だけ。
///
/// **確かめること**
///   - 下書き（draft）・試験版（prerelease）ではない
///   - タグが vX.Y.Z の形
///   - ページの URL が、この repo のそのタグのページ
///   - 添付物の URL が、この repo のそのタグの添付物（それ以外の添付物は捨てる）
///   - 添付物が上げ終わっている（state が uploaded）
/// どれかが崩れていれば、更新は無いものとして扱う（理由を返す）。
/// </summary>
public static class ReleaseParser
{
    /// <summary>応答の本文の上限。実物は 10KB 足らず（説明文が長くてもこの 100 倍には届かない）。</summary>
    public const int MaxJsonBytes = 1024 * 1024;

    public static ReleaseInfo? Parse(string json, UpdateEndpoints endpoints, out string? reason)
    {
        GitHubReleaseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, UpdateJson.Default.GitHubReleaseDto);
        }
        catch (JsonException)
        {
            reason = "応答が JSON として読めない";
            return null;
        }
        if (dto is null) { reason = "応答が空"; return null; }
        if (dto.Draft) { reason = "下書きの Release"; return null; }
        if (dto.Prerelease) { reason = "試験版の Release"; return null; }
        if (!AppVersion.TryParse(dto.TagName, out var version) || dto.TagName![0] != 'v')
        {
            reason = $"タグが vX.Y.Z の形ではない（{dto.TagName}）";
            return null;
        }
        string tag = dto.TagName;
        string expectedPage = endpoints.ReleasePagePrefix + Uri.EscapeDataString(tag);
        if (dto.HtmlUrl != expectedPage)
        {
            reason = "Release のページがこの repo の物ではない";
            return null;
        }

        var assets = new List<ReleaseAsset>();
        foreach (var a in dto.Assets ?? [])
        {
            if (a is null || string.IsNullOrEmpty(a.Name) || a.Name.IndexOfAny(['/', '\\', ':']) >= 0) continue;
            if (a.State != "uploaded" || a.Size <= 0) continue;
            var expected = endpoints.AssetUrl(tag, a.Name);
            if (a.BrowserDownloadUrl != expected.AbsoluteUri) continue;
            assets.Add(new ReleaseAsset(a.Name, a.Size, expected, DigestHex(a.Digest)));
        }
        reason = null;
        return new ReleaseInfo(version, tag, expectedPage, assets);
    }

    /// <summary>GitHub が添付物ごとに付ける "sha256:…"。無い・形が違えば null（照合には SHA256.txt を使う）。</summary>
    internal static string? DigestHex(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string hex = digest[prefix.Length..];
        return Sha256List.IsHex64(hex) ? hex.ToLowerInvariant() : null;
    }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAssetDto?>? Assets { get; set; }
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}
