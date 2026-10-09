using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartsLife.Checks;

namespace PartsLife.CheckWindow;

/// <summary>
/// `PartsLife.exe --shot-check out.png system|load|storage [拡大率] [--lang en] [--width 680]`。
/// **窓を出さずに、点検の窓の一面を PNG に書き出す。** 配布ページに載せる画像用（本体の --shot と同じ考え方）。
///
/// - 作り物ではなく本物の窓を組み立てて描く。system は管理者なしのシステムチェックを実際に走らせる
/// - load は何も走らせない（待っている面）。負荷はかけない
/// - storage は管理者の要る読み取りの前の面（ボタンと、管理者なしで読める台の一覧）。**補助 exe は起こさない**
///   （面が用意するのは WMI の台の一覧だけ。押した後の読み取り・表面検査も、書き出しの間は StoragePanel が断る）。
///   **その面を出さない版（<see cref="Features.StorageCheck"/> が false）では描かずに <see cref="FeatureDisabledException"/>**
/// - 保存（state.json）にも基準値の置き場（checks）にも書かない。シリアル番号は末尾 4 文字だけ見せる
///
/// 本体はこの型を --shot-check のときだけ触る（常駐中は読み込まない）。
/// </summary>
public static class CheckWindowShot
{
    /// <summary>この版で書き出せる面（窓に並んでいる面）。</summary>
    public static IReadOnlyList<string> Tabs => CheckWindow.VisibleKeys;

    /// <summary>
    /// 書き出して終わる。tab が知らない名前なら ArgumentException、
    /// 知っているがこの版では出さない面なら <see cref="FeatureDisabledException"/>（どちらも何も書かない）。
    /// </summary>
    public static async Task SaveAsync(string path, string tab, double scale, string lang, double width = 680)
    {
        int index = Tabs.ToList().IndexOf(tab);
        if (index < 0 && CheckWindow.AllKeys.Contains(tab)) throw new FeatureDisabledException(tab);
        if (index < 0) throw new ArgumentException($"tab は {string.Join(" / ", Tabs)} のどれか: {tab}", nameof(tab));
        scale = Math.Clamp(scale, 0.5, 4);
        width = Math.Clamp(width, 640, 1600);   // 窓の MinWidth より狭い形は、実際には無い

        L.Lang = lang == "en" ? "en" : "ja";
        Ui.ShotMode = true;
        var window = new CheckWindow();
        window.Select(index);
        await window.Panel(index).PrepareShotAsync();

        var root = (FrameworkElement)window.Content;
        // 窓の地の色は Window に付いている。中身だけを描くので、中身の側にも塗る
        if (root is Panel p) p.Background = Ui.Back;
        root.Measure(new Size(width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>シリアル番号を末尾 4 文字だけにする（書き出しのとき）。個人の機械を特定できる番号なので。</summary>
    internal static string MaskSerial(string serial)
    {
        string s = serial.Trim();
        if (s.Length <= 4) return new string('•', s.Length);
        return new string('•', Math.Min(8, s.Length - 4)) + s[^4..];
    }
}

/// <summary>
/// 知っている面だが、この版では止めてある（<see cref="Features"/>）。--shot-check はこれを受けて、
/// 描かずに 0 以外で終わる。
/// </summary>
public sealed class FeatureDisabledException : Exception
{
    public string Tab { get; }

    public FeatureDisabledException(string tab)
        : base($"--shot-check {tab}: この版では無効です（実機での確認のあと、次の版で有効にします）。 / "
             + $"disabled in this build (it will be enabled in a later version after a real-machine test).")
    {
        Tab = tab;
    }
}
