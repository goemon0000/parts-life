using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PartsParty.Rendering;
using PartsParty.Sensors;

namespace PartsParty;

public partial class MainWindow : Window
{
    private readonly Atlas _atlas = Atlas.Load();
    private readonly BoardRenderer _board;
    private readonly SystemSampler _sampler = new();

    /// <summary>拡大率。1 / 2 / 3 を回す。既定は 2（1体64px）。</summary>
    private int _scale = 2;
    private bool _expanded;
    private Snapshot _snapshot = new();

    /// <summary>絵のコマ送り。全キャラで同じ時計を使う（別々にすると揃わない）。</summary>
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _frame;

    /// <summary>
    /// 画面に並べる顔ぶれ。
    /// **v1 は固定の並び。** 実機の構成を見て組み立てるのは次の段でやる
    /// （2ソケットや GPU 無しの機械でも崩れないことを、先に見た目で確かめたいため）。
    /// </summary>
    private static readonly (string Id, SlotKind Kind)[] Layout =
    {
        ("cpu",        SlotKind.Socket),
        ("cpu-cooler", SlotKind.Socket),
        ("memory",     SlotKind.Dimm),
        ("gpu",        SlotKind.Pcie),
        ("ssd",        SlotKind.M2),
        ("hdd",        SlotKind.Sata),
        ("psu",        SlotKind.Atx),
    };

    public MainWindow()
    {
        InitializeComponent();
        _board = new BoardRenderer(_atlas);

        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        ExpandButton.Click += (_, _) => SetExpanded(!_expanded);
        SizeButton.Click += (_, _) => { _scale = _scale % 3 + 1; Redraw(); };
        TopmostButton.Click += (_, _) =>
        {
            Topmost = !Topmost;
            TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        };
        CloseButton.Click += (_, _) => Close();

        _frameTimer.Tick += (_, _) => { _frame++; Redraw(); };
        _sensorTimer.Tick += (_, _) => { _snapshot = _sampler.Sample(); UpdateDetail(); };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PlaceTopRight();
        Redraw();
        UpdateDetail();
        _frameTimer.Start();
        _sensorTimer.Start();
    }

    /// <summary>既定の位置は右上。作業領域を使うのでタスクバーに被らない。</summary>
    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Top + 16;
    }

    private void SetExpanded(bool value)
    {
        _expanded = value;
        DetailPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = value ? "▲" : "▼";
        // 展開中は数値をよく見るので、測る間隔を詰める
        _sensorTimer.Interval = TimeSpan.FromSeconds(value ? 0.5 : 1.0);
    }

    // -----------------------------------------------------------------------
    // 描画
    // -----------------------------------------------------------------------
    private void Redraw()
    {
        var slots = new List<BoardSlot>(Layout.Length);
        foreach (var (id, kind) in Layout)
            slots.Add(new BoardSlot(id, kind, MotionFor(id), _frame));

        var bmp = _board.Render(slots);
        BoardImage.Source = bmp;
        BoardImage.Width = bmp.PixelWidth * _scale;
        BoardImage.Height = bmp.PixelHeight * _scale;
        StoryPanel.Width = BoardImage.Width;
        ArrowBar.Width = BoardImage.Width;
        DetailPanel.Width = BoardImage.Width;
    }

    /// <summary>
    /// 稼働状況を動きで表す。**数字は出さない。**
    /// 畳んでいる間は「どれくらい働いているか」だけが伝わればよい。
    /// </summary>
    private string MotionFor(string id)
    {
        double load = id switch
        {
            "cpu" or "cpu-cooler" => _snapshot.CpuTotal,
            "memory" => _snapshot.MemoryRatio,
            _ => 0.0,
        };

        if (load >= 0.85) return "peek";   // 必死の顔
        if (load >= 0.45) return "walk";   // 動きが速い
        if (load <= 0.03) return "sleep";  // ほぼ何もしていない
        return "idle";
    }

    // -----------------------------------------------------------------------
    // 展開時の数値
    // -----------------------------------------------------------------------
    private void UpdateDetail()
    {
        if (!_expanded) return;
        DetailStack.Children.Clear();

        DetailStack.Children.Add(CoreRow("CPU", _snapshot.CpuCores,
            $"{_snapshot.CpuTotal * 100:0}%  {_snapshot.CpuCores.Count} 論理コア"));

        DetailStack.Children.Add(BarRow("MEM", _snapshot.MemoryRatio,
            $"{Gb(_snapshot.MemoryUsedBytes):0.0} / {Gb(_snapshot.MemoryTotalBytes):0.0} GB"));
    }

    private static double Gb(ulong bytes) => bytes / 1024.0 / 1024.0 / 1024.0;

    private static Brush LoadBrush(double v) =>
        v >= 0.85 ? new SolidColorBrush(Color.FromRgb(0xF0, 0x50, 0x70))
      : v >= 0.60 ? new SolidColorBrush(Color.FromRgb(0xE7, 0xC8, 0x4B))
                  : new SolidColorBrush(Color.FromRgb(0x5D, 0xD8, 0xBB));

    private UIElement BarRow(string label, double ratio, string text)
    {
        var grid = NewRow(label, text);
        var track = new Border
        {
            Height = 7,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = new Border
        {
            Height = 7,
            Background = LoadBrush(ratio),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(ratio, 0, 1));
        var host = new Grid();
        host.Children.Add(track);
        host.Children.Add(fill);
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);
        return grid;
    }

    /// <summary>
    /// コアの粒。**1粒＝1論理コア。**
    /// 128コアでも並べられるよう、16列で折り返す。
    /// </summary>
    private UIElement CoreRow(string label, IReadOnlyList<double> cores, string text)
    {
        var grid = NewRow(label, text);
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 16 * 6 };
        foreach (var v in cores)
        {
            wrap.Children.Add(new Border
            {
                Width = 5, Height = 5, Margin = new Thickness(0, 0, 1, 1),
                Background = LoadBrush(v),
                Opacity = 0.25 + Math.Clamp(v, 0, 1) * 0.75,
            });
        }
        Grid.SetColumn(wrap, 1);
        grid.Children.Add(wrap);
        return grid;
    }

    private static Grid NewRow(string label, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock
        {
            Text = label, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xC4)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(l, 0);
        grid.Children.Add(l);

        var t = new TextBlock
        {
            Text = text, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(t, 2);
        grid.Children.Add(t);
        return grid;
    }
}
