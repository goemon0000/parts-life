using System.Globalization;

namespace PartsLife.I18n;

/// <summary>
/// 画面の文言。物語の本文は含まない（あれは story.json 側）。
/// 既定は OS の表示言語。日本語なら ja、それ以外は en。
/// 一度でも切り替えたら、その選択を保存して以後はそれに従う。
/// </summary>
public static class Strings
{
    public static string Lang { get; set; } = DetectDefault();

    public static string DetectDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    private static string T(string ja, string en) => Lang == "en" ? en : ja;

    public static string Close        => T("終了", "Close");
    public static string SizeTip      => T("大きさ（1x / 2x / 3x に揃える）", "Size (snap to 1x / 2x / 3x)");
    public static string TopmostTip   => T("常に前面に表示", "Always on top");
    public static string LangTip      => T("言語を切り替える", "Switch language");
    public static string ExpandTip    => T("各パーツの稼働状況", "Per-part status");
    public static string CollapseTip  => T("閉じる", "Collapse");
    public static string ResizeTip    => T("ドラッグで大きさを変える", "Drag to resize");
    public static string RunAtLogin   => T("Windows起動時に開く", "Start with Windows");
    public static string LabelMenu    => T("名札", "Labels");
    public static string LabelLevelOnly    => T("レベルだけ", "Level only");
    public static string LabelNameAndLevel => T("名前とレベル", "Name and level");
    public static string LabelNone         => T("出さない", "Hide");

    public static string ResetMenu    => T("最初から", "Start over");
    public static string ChecksMenu   => T("点検…（負荷テスト・システムチェック）", "Checkup… (load test, system check)");
    public static string ResetStory   => T("物語だけ最初から", "Story only");
    public static string ResetLevels  => T("レベルだけ最初から", "Levels only");
    public static string ResetAll     => T("両方とも最初から", "Both");
    public static string ResetTitle   => T("最初からやり直しますか", "Start over?");

    public static string ResetStoryAsk => T(
        "物語を最初の章に戻します。各パーツのレベルはそのまま残ります。\n\nこの操作は取り消せません。",
        "The story goes back to its first chapter. Part levels are kept.\n\nThis cannot be undone.");
    public static string ResetLevelsAsk => T(
        "各パーツのレベルを 1 に戻します。物語の進み具合はそのまま残ります。\n\nこの操作は取り消せません。",
        "Every part goes back to level 1. Story progress is kept.\n\nThis cannot be undone.");
    public static string ResetAllAsk => T(
        "物語もレベルも最初に戻します。窓の位置と言語の設定は残ります。\n\nこの操作は取り消せません。",
        "Both the story and every level go back to the start. Window position and language are kept.\n\nThis cannot be undone.");

    public static string CoresSuffix(int n) => T($"{n} 論理コア", $"{n} logical cores");
    public static string NoGpu    => T("GPUが見つかりません", "No GPU detected");
    public static string Vram     => T("VRAM", "VRAM");
    public static string ReadWrite=> T("読み / 書き", "Read / Write");
    public static string M2       => T("M.2", "M.2");
    public static string Ssd      => T("SSD", "SSD");
    public static string Hdd      => T("HDD", "HDD");
    public static string Drives   => T("ドライブ", "Drives");
    public static string Level    => T("Lv", "Lv");
    public static string Gear     => T("装備", "Gear");

    // ---- 更新 -------------------------------------------------------------
    public static string UpdateMenu       => T("更新", "Updates");
    public static string UpdateCurrent(string v) => T($"いまの版 v{v}", $"Current version v{v}");
    public static string UpdateLastCheck(DateTimeOffset t) =>
        T($"最後の確認 {t.ToLocalTime():yyyy-MM-dd HH:mm}", $"Last checked {t.ToLocalTime():yyyy-MM-dd HH:mm}");
    public static string UpdateNeverChecked => T("まだ確認していません", "Not checked yet");
    public static string UpdateCheckNow   => T("今すぐ確認", "Check now");
    public static string UpdateChecking   => T("確認しています…", "Checking…");
    public static string UpdateCheckOffTip => T("「確認しない」の間は通信しません", "No network access while set to \"Don't check\"");
    public static string UpdateModeAuto   => T("自動で更新する", "Update automatically");
    public static string UpdateModeNotify => T("確認だけして知らせる", "Check and notify only");
    public static string UpdateModeOff    => T("確認しない", "Don't check");
    public static string UpdateNotes      => T("更新の内容（GitHub）", "Release notes (GitHub)");
    public static string UpdateWhatsNew   => T("内容を見る", "What's new");
    public static string UpdateStaged(string v) => T($"v{v} を用意しました（手が空いたら入れ替えます）", $"v{v} is ready (installs when idle)");
    public static string UpdateAvailable(string v) => T($"新しい版 v{v} があります", $"A new version v{v} is available");
    public static string UpdateAvailableBlocked(string v, string why) =>
        T($"新しい版 v{v} があります（{why}）", $"A new version v{v} is available ({why})");
    public static string UpdateUpdated(string v)   => T($"v{v} に更新しました", $"Updated to v{v}");
    public static string UpdateUpToDate(string v)  => T($"最新の版です（v{v}）", $"You're up to date (v{v})");
    public static string UpdateFailed(string why)  => T($"更新を確かめられませんでした: {why}", $"Update check failed: {why}");
    public static string UpdateRateLimited => T("GitHub の回数制限のため、しばらく確認しません", "GitHub rate limit reached; checking again later");
    public static string UpdateSkipped(string v)   => T($"v{v} は前にうまく起動しなかったので入れません", $"v{v} failed to start before; not installing it");
    public static string UpdateRolledBack(string v, string cur) =>
        T($"v{v} がうまく起動しなかったので、v{cur} に戻しました", $"v{v} didn't start properly; went back to v{cur}");
    public static string UpdateAlreadyInstalled(string v) =>
        T($"新しい版 v{v} に入れ替わっています。開き直すと新しい版になります", $"v{v} is already installed; reopen the app to use it");
    public static string UpdateGaveUp(string v) =>
        T($"v{v} から元の版に戻せませんでした。PartsLife.exe.old を PartsLife.exe に戻してください",
          $"Couldn't go back from v{v}. Please rename PartsLife.exe.old to PartsLife.exe");
    public static string UpdateRestored =>
        T("更新を途中で止めたので、元の版のまま動いています（次の確認でやり直します）", "The update was interrupted; still on the previous version (will retry later)");
    public static string UpdateStuck(string why) =>
        T($"更新を元に戻し切れませんでした（{why}）。このまま動きます。PartsLife.exe.old があれば PartsLife.exe に戻してください",
          $"Couldn't finish undoing the update ({why}). Running as is; if PartsLife.exe.old exists, rename it to PartsLife.exe");
    public static string UpdateBusy =>
        T("更新の途中です。少し待ってからもう一度起動してください", "An update is in progress. Please wait a moment and start again.");
    public static string UpdateSwapFailed(string why) => T($"入れ替えられませんでした: {why}", $"Couldn't install the update: {why}");

    internal static string CannotReplace(Updates.ReplaceBlock b) => b switch
    {
        Updates.ReplaceBlock.ExeName => T("exe の名前が PartsLife.exe ではないため、自動では入れ替えられません",
                                          "the exe isn't named PartsLife.exe, so it can't be replaced automatically"),
        Updates.ReplaceBlock.DevBuild => T("開発用のビルドのため、自動では入れ替えません",
                                           "this is a development build, so it isn't replaced automatically"),
        _ => T("このフォルダには書き込めないため、自動では入れ替えられません",
               "this folder isn't writable, so it can't be replaced automatically"),
    };

    public static string Disclaimer => T(
        "※ 規格・相性・物語はすべてお遊びです。購入や設定の判断には使えません。",
        "Note: the specs, compatibility marks and story here are all for fun — not a reference for purchases or settings.");
}
