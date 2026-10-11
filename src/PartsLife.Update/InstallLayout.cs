namespace PartsLife.Update;

/// <summary>
/// いま置かれている形。**配ったときの形のまま更新する。**
///   Zip       … PartsLife.exe の隣に PartsLife.Check.exe がある（PartsLife.zip を展開した形）→ PartsLife.zip を落とす
///   SingleExe … PartsLife.exe だけ（展開せずに動く版）→ PartsLife.exe を落とす
/// </summary>
public enum InstallLayout { Zip, SingleExe }

public static class InstallLayouts
{
    public const string MainExe = "PartsLife.exe";
    public const string HelperExe = "PartsLife.Check.exe";
    public const string ZipAsset = "PartsLife.zip";
    public const string ShaAsset = "SHA256.txt";

    public static InstallLayout Detect(string appDir) =>
        File.Exists(Path.Combine(appDir, HelperExe)) ? InstallLayout.Zip : InstallLayout.SingleExe;

    /// <summary>落とす添付物の名前。</summary>
    public static string AssetFor(InstallLayout layout) => layout == InstallLayout.Zip ? ZipAsset : MainExe;

    /// <summary>入れ替える物（ZIP の中身もこの 2 つだけ）。</summary>
    public static IReadOnlyList<string> FilesFor(InstallLayout layout) =>
        layout == InstallLayout.Zip ? [MainExe, HelperExe] : [MainExe];

    public static string Name(InstallLayout layout) => layout == InstallLayout.Zip ? "zip" : "exe";

    public static InstallLayout? Parse(string? name) => name switch
    {
        "zip" => InstallLayout.Zip,
        "exe" => InstallLayout.SingleExe,
        _ => null,
    };
}
