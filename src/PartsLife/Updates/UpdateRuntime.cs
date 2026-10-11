using System.Diagnostics;
using System.IO;
using PartsLife.Game;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 保存の置き場。**Debug ビルドだけ**、環境変数 <see cref="TestDataVariable"/> で一時フォルダへ逃がせる
/// （更新の通しの確かめ tools/update-dry-run.py が、本物の進行と更新の覚え書きを触らないように）。
/// </summary>
internal static class AppData
{
    public const string TestDataVariable = "PARTSLIFE_TEST_DATA_DIR";

#if DEBUG
    public static string? TestDataDir =>
        Environment.GetEnvironmentVariable(TestDataVariable) is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d : null;
#else
    public static string? TestDataDir => null;
#endif

    public static PartyState LoadState() => TestDataDir is { } d ? PartyState.Load(d) : PartyState.Load();

    public static string UpdatesRoot => TestDataDir is { } d ? Path.Combine(d, "updates") : UpdateStore.DefaultRoot;

    /// <summary>
    /// 試験用: この版として起動したら、3 秒後に落ちる（入れ替えた新しい版が起動直後に落ちる場面を、本物のアプリで作るため）。
    /// **Debug ビルドだけ。** 配布物（Release）では中身が空で、環境変数を見もしない。
    /// </summary>
    public const string TestCrashVariable = "PARTSLIFE_TEST_CRASH_VERSION";

    [System.Diagnostics.Conditional("DEBUG")]
    public static void CrashForTest(AppVersion current)
    {
#if DEBUG
        if (TestDataDir is not null && Environment.GetEnvironmentVariable(TestCrashVariable) == current.ToString())
            _ = Task.Run(async () => { await Task.Delay(3000); Environment.Exit(3); });
#endif
    }
}

/// <summary>入れ替えられない理由（<see cref="UpdateRuntime.Block"/>）。</summary>
internal enum ReplaceBlock { None, ExeName, DevBuild, ReadOnlyFolder }

/// <summary>
/// いま動いているこのアプリの、更新に要る事情（置き場所・版・入れ替えられるか）。起動のたびに 1 回作る。
/// </summary>
internal sealed class UpdateRuntime
{
    public required string AppDir { get; init; }
    public required string ExePath { get; init; }
    public required AppVersion Current { get; init; }
    public required UpdateStore Store { get; init; }
    public required UpdateEndpoints Endpoints { get; init; }

    /// <summary>入れ替えの最中の印。試験用の保存の置き場（Debug だけ）では名前を分け、普段のアプリと干渉しない。</summary>
    public required UpdateLock Lock { get; init; }

    /// <summary>入れ替えられない理由。None なら入れ替えられる（そのときだけ「自動で更新する」が働く）。</summary>
    public ReplaceBlock Block { get; init; }

    public bool CanReplace => Block == ReplaceBlock.None;

    public InstallLayout Layout => InstallLayouts.Detect(AppDir);

    /// <summary>後片付けで見る名前（置き方に関わらず両方）。</summary>
    public static readonly IReadOnlyList<string> AllFiles = [InstallLayouts.MainExe, InstallLayouts.HelperExe];

    public static UpdateRuntime? Create()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        string dir = Path.GetDirectoryName(exe)!;
        return new UpdateRuntime
        {
            AppDir = dir,
            ExePath = exe,
            Current = AppVersion.From(typeof(UpdateRuntime).Assembly.GetName().Version),
            Store = new UpdateStore(AppData.UpdatesRoot),
            Endpoints = UpdateEndpoints.FromTestFeedVariable() ?? UpdateEndpoints.GitHub,
            Block = WhyCannotReplace(exe, dir),
            Lock = new UpdateLock(AppData.TestDataDir is { } t
                ? UpdateLock.DefaultName + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                      System.Text.Encoding.UTF8.GetBytes(t.ToLowerInvariant())))[..16]
                : UpdateLock.DefaultName),
        };
    }

    /// <summary>
    /// 入れ替えられない理由。
    ///   - exe の名前が PartsLife.exe でない（「PartsLife (1).exe」など）→ 名前で入れ替えるので、違う物を替えてしまう
    ///   - 隣に PartsLife.dll がある → 開発用のビルド（本体は dll の側。exe だけ替えても意味が無い）
    ///   - フォルダに書けない（Program Files など）
    /// どれでも「確認だけして知らせる」と同じ動きになり、そのことを利用者に伝える。
    /// </summary>
    private static ReplaceBlock WhyCannotReplace(string exe, string dir)
    {
        if (!string.Equals(Path.GetFileName(exe), InstallLayouts.MainExe, StringComparison.OrdinalIgnoreCase))
            return ReplaceBlock.ExeName;
        if (File.Exists(Path.Combine(dir, "PartsLife.dll"))) return ReplaceBlock.DevBuild;
        if (!Swapper.IsWritable(dir)) return ReplaceBlock.ReadOnlyFolder;
        return ReplaceBlock.None;
    }

    public UpdateContext Context() => new()
    {
        AppDir = AppDir,
        Current = Current,
        Store = Store,
        Endpoints = Endpoints,
        // 理由の文は画面の側で言語に合わせて出す。ここでは「入れ替えられない」ことだけ伝える
        CannotReplaceReason = CanReplace ? null : Block.ToString(),
    };

    /// <summary>この exe（入れ替えた後なら新しい版）を起こす。</summary>
    public Process? Launch(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(AppDir, InstallLayouts.MainExe))
            {
                UseShellExecute = false,
                WorkingDirectory = AppDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
