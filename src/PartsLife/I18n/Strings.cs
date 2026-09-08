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

    public static string Disclaimer => T(
        "※ 規格・相性・物語はすべてお遊びです。購入や設定の判断には使えません。",
        "Note: the specs, compatibility marks and story here are all for fun — not a reference for purchases or settings.");
}
