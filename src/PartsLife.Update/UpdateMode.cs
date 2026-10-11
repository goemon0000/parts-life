namespace PartsLife.Update;

/// <summary>更新の設定。保存では "auto" / "notify" / "off" の文字で持つ（state.json の UpdateMode）。</summary>
public enum UpdateMode
{
    /// <summary>自動で更新する（既定）。確かめて、落として、照合して、入れ替える。</summary>
    Auto,
    /// <summary>確認だけして知らせる。落とさない。</summary>
    NotifyOnly,
    /// <summary>確認しない。**通信を一切しない。**</summary>
    Off,
}

public static class UpdateModes
{
    public const string AutoName = "auto";
    public const string NotifyName = "notify";
    public const string OffName = "off";

    /// <summary>
    /// 保存の文字から。**空（この欄が無い古い保存）は既定の Auto。**
    /// 知らない文字（新しい版が足した設定など）は、勝手に入れ替えまではしない NotifyOnly に読む。
    /// </summary>
    public static UpdateMode Parse(string? name) => name switch
    {
        null or "" or AutoName => UpdateMode.Auto,
        NotifyName => UpdateMode.NotifyOnly,
        OffName => UpdateMode.Off,
        _ => UpdateMode.NotifyOnly,
    };

    public static string Name(UpdateMode mode) => mode switch
    {
        UpdateMode.NotifyOnly => NotifyName,
        UpdateMode.Off => OffName,
        _ => AutoName,
    };
}
