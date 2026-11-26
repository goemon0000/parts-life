using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartsLife.I18n;
using PartsLife.Update;

namespace PartsLife.Updates;

/// <summary>
/// 最後の版（v0.8.0）の「パーツぐらしは PCミエルの一部になりました」の案内。**一度だけ**出す（既読の記録は
/// <see cref="FinalNoticeStore"/>。決まりはそちらで試験している）。
///   - 「PCミエルの紹介を開く」: 既定のブラウザーで紹介のページを開く。**何も入れず、Run の設定にも触れない**
///   - 「閉じる」・窓の ✕: 閉じる。**✕ も「閉じる」と同じ道を通る**（既読を書いてから閉じる）
///   - 書けなかったら、その旨を窓に出して**開いたまま**にし、既読にしない（次回もう一度出す）。黙って既読にしない。
///     もう一度押すと、書けなくても閉じる（既読にはならない）
///   - 既読の記録の読み書きは画面のスレッドの外（Task.Run）。結果を受けて画面のスレッドで動く
/// 出すのは主画面を出したあと。入れ替えで起こされた新しい版は、「起動できた」（Confirmed）を書き終えてから（UpdateController）。
/// </summary>
internal static class FinalNoticeUi
{
    private static Window? _open;

    /// <summary>未読なら出す（窓が出たあとに呼ぶ）。</summary>
    public static void ShowIfUnread(Window owner) => _ = ShowIfUnreadAsync(owner);

    private static async Task ShowIfUnreadAsync(Window owner)
    {
        var store = new FinalNoticeStore(AppData.FinalNoticeDir);
        bool read = await Task.Run(store.IsRead);   // ファイルを読むのは画面のスレッドの外。await のあとは画面のスレッドに戻る
        if (read || owner.Dispatcher.HasShutdownStarted) return;
        Show(owner);
    }

    /// <summary>既読でも出す（更新の欄の「もう一度見る」）。出ている最中なら前に出すだけ。</summary>
    public static void Show(Window owner)
    {
        if (_open is { } w)
        {
            w.Activate();
            return;
        }
        var flow = new Flow(new FinalNoticeStore(AppData.FinalNoticeDir));
        var win = new Window
        {
            Title = "パーツぐらし",
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = BuildContent(flow.OnOpen, flow.OnClose, out var error),
        };
        flow.Attach(win, error);
        owner.Closing += (_, _) => flow.OwnerClosing = true;   // アプリが終わるときに窓が畳まれるのは「閉じる」ではない
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.Show();   // 常駐の窓は止めない（モードレス）
    }

    /// <summary>窓の中身。実際の窓と `--shot-notice` で同じ物。</summary>
    internal static FrameworkElement BuildContent(Action onOpen, Action onClose, out TextBlock errorLine)
    {
        var text = new TextBlock
        {
            Text = Strings.FinalNoticeText,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)),
        };
        errorLine = new TextBlock
        {
            Text = Strings.FinalNoticeNotRecorded,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8)),
            Visibility = Visibility.Collapsed,
        };
        var open = new Button { Content = Strings.FinalNoticeOpen, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var close = new Button { Content = Strings.FinalNoticeClose, Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        open.Click += (_, _) => onOpen();
        close.Click += (_, _) => onClose();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(errorLine);
        stack.Children.Add(buttons);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x19)),
            Padding = new Thickness(18, 16, 18, 14),
            Child = stack,
        };
    }

    /// <summary>
    /// `--shot-notice` 用。案内の窓の中身を PNG にして書く。**保存も既読の記録も読まず・書かず、Mutex も取らず、通信もしない。**
    /// </summary>
    internal static void SaveShot(string path, string lang, double scale)
    {
        Strings.Lang = lang == "en" ? "en" : "ja";
        var content = BuildContent(() => { }, () => { }, out _);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
                                         96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    /// <summary>ボタンと窓の ✕ の動き。1 つの案内に 1 つ。</summary>
    private sealed class Flow(FinalNoticeStore store)
    {
        private Window? _win;
        private TextBlock? _error;
        private bool _failedOnce;
        private bool _done;

        public bool OwnerClosing { get; set; }

        public void Attach(Window win, TextBlock error)
        {
            _win = win;
            _error = error;
            // 窓の ✕ は「閉じる」と同じ道を通る（アプリごと終わるときを除く）。いったん閉じるのを止め、書けたら閉じる。
            // 書けなかったら開いたまま失敗の行を出し、もう一度の ✕ は（既読にならないまま）閉じる
            win.Closing += (_, e) =>
            {
                if (_done || OwnerClosing) return;
                e.Cancel = true;
                OnClose();
            };
        }

        public void OnOpen()
        {
            try { Process.Start(new ProcessStartInfo(FinalNoticeStore.PageUrl) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            Record(closeEvenIfFailed: false);
        }

        public void OnClose() => Record(closeEvenIfFailed: _failedOnce);

        private bool _busy;

        /// <summary>既読を書く（画面のスレッドの外）。書けたら閉じる。書けなかったら窓に出して開いたまま（もう一度押すと、書けなくても閉じる）。</summary>
        private void Record(bool closeEvenIfFailed) => _ = RecordAsync(closeEvenIfFailed);

        private async Task RecordAsync(bool closeEvenIfFailed)
        {
            if (_busy) return;   // 書いている最中の押し直しは受けない
            _busy = true;
            bool ok;
            try { ok = await Task.Run(() => store.TryMarkRead(DateTimeOffset.UtcNow, out _)); }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                ok = false;
            }
            _busy = false;
            if (ok)
            {
                _done = true;
                _win?.Close();
                return;
            }
            _failedOnce = true;
            if (_error is not null) _error.Visibility = Visibility.Visible;
            if (closeEvenIfFailed)
            {
                _done = true;
                _win?.Close();
            }
        }
    }
}
