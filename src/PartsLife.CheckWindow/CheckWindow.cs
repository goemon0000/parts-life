using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PartsLife.Checks;

namespace PartsLife.CheckWindow;

/// <summary>
/// 本体から呼ぶ入口。**本体はこの型を、献立で「点検」を押したときに初めて触る。**
/// （それまでこのアセンブリは読み込まれない＝常駐の負担は 0）
/// </summary>
public static class CheckWindowHost
{
    private static CheckWindow? _open;

    /// <param name="lang">"ja" / "en"。本体の言語設定に合わせる。</param>
    public static void Open(string lang)
    {
        if (_open is not null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        L.Lang = lang == "en" ? "en" : "ja";
        _open = new CheckWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    public static bool IsOpen => _open is not null;
}

/// <summary>
/// 「点検」の窓。面（システムチェック・負荷テスト・記憶媒体）を切り替える。
/// **出す面は <see cref="Features"/> で決まる。** v0.7.0 は記憶媒体の面を出さない（2面）。
///
/// - 開いただけでは何も動かない。各面のボタンを押したときだけ動く
/// - 閉じたら全部止め、止まり終わったところでメモリを返す
/// </summary>
internal sealed class CheckWindow : Window
{
    /// <summary>
    /// 面の一覧（書き出しの名前・題・この版で出すか・作り方）。出さない面は**作りもしない**
    /// （記憶媒体の面は作ると補助 exe を探しに行くため）。
    /// </summary>
    private static (string Key, Func<string> Title, bool On, Func<CheckPanel> Make)[] Faces() => new (string, Func<string>, bool, Func<CheckPanel>)[]
    {
        ("system", () => T.TabSystem, true, () => new SystemPanel()),
        ("load", () => T.TabLoad, true, () => new LoadPanel()),
        ("storage", () => T.TabStorage, Features.StorageCheck, () => new StoragePanel()),
    };

    /// <summary>面の名前の全部（--shot-check が知っている名前）。</summary>
    internal static IReadOnlyList<string> AllKeys { get; } = Faces().Select(f => f.Key).ToArray();

    /// <summary>この版で出す面の名前。窓に並ぶ順。</summary>
    internal static IReadOnlyList<string> VisibleKeys { get; } = Faces().Where(f => f.On).Select(f => f.Key).ToArray();

    private readonly CheckPanel[] _panels;
    private readonly ToggleTab[] _tabs;
    private readonly ContentControl _body = new();

    public CheckWindow()
    {
        Title = T.WindowTitle;
        Width = 820;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        Background = Ui.Back;
        Foreground = Ui.Text;
        // 英語のときは Segoe UI を先に。和文の書体を先にすると、"F:\pagefile.sys" の「\」が「¥」で描かれる
        // （和文が混ざれば、その字だけ後ろの書体で描かれる）
        FontFamily = new FontFamily(L.Lang == "en" ? "Segoe UI, Yu Gothic UI, Meiryo UI" : "Yu Gothic UI, Meiryo UI, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        var faces = Faces().Where(f => f.On).ToArray();
        _panels = faces.Select(f => f.Make()).ToArray();
        _tabs = faces.Select((f, i) => new ToggleTab(f.Title(), i)).ToArray();

        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 0) };
        foreach (var t in _tabs)
        {
            var captured = t;
            t.Button.Click += (_, _) => Select(captured.Index);
            tabRow.Children.Add(t.Button);
        }

        var header = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        header.Children.Add(Ui.Label(T.WindowHeading, 16, Ui.Text, bold: true));
        header.Children.Add(Ui.Label(T.WindowSub, 12, Ui.Sub));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 10, 16, 16),
            Content = _body,
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(tabRow, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(tabRow);
        root.Children.Add(scroll);
        Content = root;

        Select(0);
        Closing += (_, _) => { foreach (var p in _panels) p.Stop(); };
        Closed += async (_, _) => await ReleaseAsync();
    }

    internal CheckPanel Panel(int index) => _panels[index];

    /// <summary>並んでいる面の題（試験で、出している面を確かめるため）。</summary>
    internal IReadOnlyList<string> TabTitles => _tabs.Select(t => (string)t.Button.Content).ToArray();

    internal void Select(int index)
    {
        for (int i = 0; i < _tabs.Length; i++) _tabs[i].Button.IsChecked = i == index;
        _body.Content = _panels[index];
    }

    /// <summary>
    /// 閉じた後の片付け。動いていた試験が止まり終わるのを待ってから（最大 15 秒）、
    /// 使った領域を GC に返させる。メモリ試験の領域はその時点で既に解放済み（試験側の finally）。
    /// </summary>
    private async Task ReleaseAsync()
    {
        var running = Task.WhenAll(_panels.Select(p => p.Running));
        await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(15)));
        _body.Content = null;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class ToggleTab
    {
        public System.Windows.Controls.Primitives.ToggleButton Button { get; }
        public int Index { get; }

        public ToggleTab(string text, int index)
        {
            Index = index;
            var b = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 6, 0),
                Foreground = Ui.Text, Background = Ui.Panel, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1),
                FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
            };
            // 見た目は Ui.Button と同じ平らな形（選ばれている面は薄緑）
            b.Template = Ui.Button("").Template;
            Button = b;
        }
    }
}
