namespace PartsLife.Checks.Build;

/// <summary>
/// 部品1種類（同じ物が何枚あるかは <see cref="Q"/>）。構成コードの1要素にそのまま対応する。
/// **型番と数字だけ。** シリアル番号・ドライブ文字・PC の名前は、そもそも持つ場所が無い。
/// </summary>
/// <param name="Name">OS が報告する型番（`n`）。</param>
/// <param name="Cap">容量 GB（`cap`）。メモリは1枚あたり、記憶装置は10進の GB。</param>
/// <param name="Speed">メモリの定格 MT/s（`spd`）。</param>
/// <param name="Type">メモリの種類（DDR4 など）か、記憶装置の接続（nvme / sata）（`t`）。</param>
/// <param name="Mfr">メーカー（`mfr`）。</param>
/// <param name="Vram">GPU のメモリ GB（`vram`）。</param>
/// <param name="Q">枚数（`q`）。1 のときは書かない。</param>
public sealed record BuildPart(string Name, int? Cap = null, int? Speed = null, string? Type = null,
                               string? Mfr = null, int? Vram = null, int Q = 1)
{
    /// <summary>同じ部品かどうかの鍵（枚数以外の全部）。</summary>
    internal BuildPart Key => this with { Q = 1 };
}

/// <summary>
/// 機械1台ぶんの部品の一覧。分類ごとの並び。**読めなかった分類は空のまま**（例外にしない）。
/// </summary>
public sealed class BuildConfig
{
    /// <summary>
    /// このアプリが読める分類と、構成コードに書く順。
    /// 受け手（web-autoai.com の /my-config）が知っている分類名だけを使う。
    /// </summary>
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "cpu", "gpu", "motherboard", "ram", "ssd", "hdd", "monitor", "optical-drive",
    };

    private readonly Dictionary<string, List<BuildPart>> _parts = new();

    public IReadOnlyList<BuildPart> this[string category] =>
        _parts.TryGetValue(category, out var l) ? l : Array.Empty<BuildPart>();

    public void Set(string category, IEnumerable<BuildPart> parts)
    {
        if (!Categories.Contains(category)) throw new ArgumentException($"知らない分類: {category}", nameof(category));
        _parts[category] = parts.ToList();
    }

    public void Remove(string category) => _parts.Remove(category);

    /// <summary>部品の入っている分類（書く順）。</summary>
    public IEnumerable<string> NonEmpty => Categories.Where(c => this[c].Count > 0);

    /// <summary>枚数の合計（q を足す）。</summary>
    public int Count(string category) => this[category].Sum(p => p.Q);

    public bool IsEmpty => !NonEmpty.Any();

    public BuildConfig Clone()
    {
        var c = new BuildConfig();
        foreach (var (k, v) in _parts) c._parts[k] = v.ToList();
        return c;
    }
}
