using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PartsLife.Game;
using PartsLife.I18n;
using PartsLife.Rendering;
using PartsLife.Sensors;

namespace PartsLife;

public partial class MainWindow : Window
{
    private readonly Atlas _atlas = Atlas.Load();
    private readonly BoardRenderer _board;
    private readonly SystemSampler _sampler = new();
    private readonly PdhSampler _pdh = new();
    private readonly DiskSampler _disk = new();

    private readonly PartyState _state = PartyState.Load();
    private readonly Director _director;

    /// <summary>
    /// 絵の拡大率。**整数でなくてもよい。**
    /// 掴んで引けば自由に変わり、⤢ で整数（＝ドットが崩れない値）に揃う。
    /// </summary>
    private double _scale = 2.0;

    /// <summary>数値欄がこれより狭くなると、GPU の型番が入らない。</summary>
    private const double MinPanelWidth = 520;

    private bool _expanded;
    private int _boardPixelWidth;

    /// <summary>名札の出し方。パーツが増えると字が邪魔になる人もいるので選べるようにする。</summary>
    private enum Labels { LevelOnly, NameAndLevel, None }
    private Labels _labels = Labels.LevelOnly;
    private Snapshot _snapshot = new();
    private IReadOnlyList<GpuInfo> _gpus = Array.Empty<GpuInfo>();
    private DiskThroughput _throughput = DiskThroughput.Zero;
    private IReadOnlyList<DriveInfoLite> _drives = Array.Empty<DriveInfoLite>();
    private Work _work = new(0, 0, 0, 0, 0);

    /// <summary>絵のコマ送り。全キャラで同じ時計を使う（別々にすると揃わない）。</summary>
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastSave = DateTime.UtcNow;
    private int _frame;

    private readonly List<TextBlock> _levelLabels = new();

    /// <summary>重ねたときに出す小さな札。WPF の ToolTip は遅延と再表示の癖が強いので自前で持つ。</summary>
    private readonly System.Windows.Controls.Primitives.Popup _hoverTip = new()
    {
        AllowsTransparency = true,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
    };
    private readonly TextBlock _hoverTipText = new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xF8)),
        LineHeight = 15,
    };

    /// <summary>
    /// いま盤に立っている顔ぶれ。**実機の構成から毎秒組み立て直す。**
    /// 固定の並びにしていたため、GPU が2枚の機械でも GPU くんが1体しか立たなかった。
    /// </summary>
    private List<PartySlot> _slots = new();

    /// <summary>CPU ごとの担当コア。起動時に1回だけ数える。</summary>
    private readonly IReadOnlyList<int[]> _packages;

    public MainWindow()
    {
        InitializeComponent();
        _board = new BoardRenderer(_atlas);
        _hoverTip.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x13, 0x19)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x5D, 0xD8, 0xBB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 5),
            Child = _hoverTipText,
        };
        _director = new Director(Story.Load(), _state);
        _packages = TopologySampler.CpuPackages(_sampler.LogicalProcessorCount);
        _state.MigrateKeys();
        _labels = _state.Labels switch
        {
            "name" => Labels.NameAndLevel,
            "none" => Labels.None,
            _ => Labels.LevelOnly,
        };

        Strings.Lang = _state.Lang.Length > 0 ? _state.Lang : Strings.DetectDefault();
        _scale = _state.Zoom > 0 ? Math.Clamp(_state.Zoom, 0.8, 6.0) : 2.0;
        Topmost = _state.Topmost;

        Loaded += OnLoaded;
        Closing += (_, _) => SaveState();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        ExpandButton.Click += (_, _) => SetExpanded(!_expanded);
        SizeButton.Click += (_, _) => SnapScale();
        LangButton.Click += (_, _) =>
        {
            Strings.Lang = Strings.Lang == "ja" ? "en" : "ja";
            _state.Lang = Strings.Lang;
            ApplyLanguage();
        };
        TopmostButton.Click += (_, _) =>
        {
            Topmost = !Topmost;
            _state.Topmost = Topmost;
            TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        };
        CloseButton.Click += (_, _) => Close();

        Grip.DragDelta += OnGripDrag;
        BoardHost.MouseMove += OnBoardHover;
        BoardHost.MouseLeave += (_, _) => _hoverTip.IsOpen = false;
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };

        _frameTimer.Tick += (_, _) => { _frame++; Redraw(); };
        _sensorTimer.Tick += (_, _) => OnSensorTick();
    }

    /// <summary>
    /// 窓を出さずに、いまの見た目そのままを1枚の絵にする。配布ページに載せる画像用。
    /// **作り物のモックではなく、本物の描画をそのまま使う**ため、
    /// 画面と宣材が食い違うことがない。
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderShot(bool expanded, double scale, string? lang = null)
    {
        _scale = scale;
        if (lang is "ja" or "en")
        {
            Strings.Lang = lang;
            // 過去の行は書いた時の言語のまま残っている。宣材に混ざると見苦しいので流す
            _state.Log.Clear();
        }
        ApplyLanguage();
        OnSensorTick();
        System.Threading.Thread.Sleep(1100);   // PDH は2回目の収集で初めて値が出る
        OnSensorTick();
        SetExpanded(expanded);
        Grip.Visibility = Visibility.Collapsed;   // 宣材に掴みは要らない
        Redraw();

        Shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Shell.Arrange(new Rect(Shell.DesiredSize));
        Shell.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(Shell.DesiredSize.Width), (int)Math.Ceiling(Shell.DesiredSize.Height),
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(Shell);
        return rtb;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyLanguage();
        TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        OnSensorTick();
        Redraw();
        SetExpanded(_state.Expanded);
        PlaceWindow();
        _frameTimer.Start();
        _sensorTimer.Start();
    }

    /// <summary>前回の位置を覚えていればそこへ。無ければ右上。</summary>
    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        if (_state.WindowLeft >= 0 && _state.WindowTop >= 0)
        {
            Left = Math.Min(_state.WindowLeft, area.Right - 80);
            Top = Math.Min(_state.WindowTop, area.Bottom - 60);
        }
        else
        {
            Left = area.Right - ActualWidth - 16;
            Top = area.Top + 16;
        }
    }

    private void SetExpanded(bool value)
    {
        _expanded = value;
        _state.Expanded = value;
        DetailPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = value ? "▲" : "▼";
        ExpandButton.ToolTip = value ? Strings.CollapseTip : Strings.ExpandTip;
        // 展開中は数値をよく見るので、測る間隔を詰める
        _sensorTimer.Interval = TimeSpan.FromSeconds(value ? 0.5 : 1.0);
        UpdateDetail();
    }

    // -----------------------------------------------------------------------
    // 大きさ
    // -----------------------------------------------------------------------

    /// <summary>
    /// 掴んで引いた分を拡大率に直す。横に引いた量で決める
    /// （縦横を別々に伸ばすと、ドット絵の縦横比が崩れて別物に見える）。
    /// </summary>
    private void OnGripDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double baseWidth = BoardRenderer.BoardWidth(BoardRenderer.PerRow(_slots.Count));
        _scale = Math.Clamp(_scale + e.HorizontalChange / baseWidth, 0.8, 6.0);
        Redraw();
        KeepOnScreen();
    }

    /// <summary>
    /// ⤢: **物理ピクセルで整数倍になる値**に揃える。
    /// 拡大率が半端だと、ドットの太さが場所によって 2px と 3px に割れて汚くなる。
    /// 表示倍率 125% の画面では 1.6 倍が「物理2倍」なので、そこへ寄せる。
    /// </summary>
    private void SnapScale()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1;
        int n = (int)Math.Round(_scale * dpi);
        n = n % 3 + 1;                       // 1 → 2 → 3 → 1 と回す
        _scale = n / dpi;
        Redraw();
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        if (Left + ActualWidth > area.Right) Left = Math.Max(area.Left, area.Right - ActualWidth);
        if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    // -----------------------------------------------------------------------
    // 描画
    // -----------------------------------------------------------------------
    private void Redraw()
    {
        if (_slots.Count == 0) return;

        var slots = new List<BoardSlot>(_slots.Count);
        foreach (var s in _slots)
            slots.Add(new BoardSlot(s.CharacterId, s.Kind, MotionFor(s.Load), _frame,
                                    _state.Level(s.Key), _state.LevelProgress(s.Key), s.Load));

        var bmp = _board.Render(slots, (int)Math.Ceiling(MinPanelWidth / Math.Max(_scale, 0.1)));
        BoardImage.Source = bmp;
        double w = Math.Round(bmp.PixelWidth * _scale);
        double h = Math.Round(bmp.PixelHeight * _scale);
        BoardImage.Width = w;
        BoardImage.Height = h;
        BoardHost.Width = w;
        BoardHost.Height = h;
        // **枠の幅は盤の幅と切り離す。**
        // 段を折り返すと盤は細くなるが、数値欄まで一緒に細くすると
        // GPU の型番が入らなくなる。狭いときは枠だけ広げて、盤を中央に置く。
        double panel = Math.Max(w, MinPanelWidth);
        _boardPixelWidth = bmp.PixelWidth;
        StoryPanel.Width = panel;
        ArrowBar.Width = panel;
        DetailPanel.Width = panel;

        PlaceLevelLabels();
    }

    /// <summary>レベルの字を、基板の帯の上に置く。字は絵と違って拡大せず、常に読める大きさで。</summary>
    private void PlaceLevelLabels()
    {
        while (_levelLabels.Count < _slots.Count)
        {
            var t = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xE8, 0xDD)),
                FontWeight = FontWeights.SemiBold,
            };
            _levelLabels.Add(t);
            LevelLayer.Children.Add(t);
        }
        for (int i = _slots.Count; i < _levelLabels.Count; i++) _levelLabels[i].Text = "";

        double font = Math.Clamp(6 + 3 * _scale, 9, 15);
        double cell = BoardRenderer.Cell * _scale;
        int perRow = BoardRenderer.PerRow(_slots.Count);

        for (int i = 0; i < _slots.Count; i++)
        {
            var t = _levelLabels[i];
            t.FontSize = font;
            t.Width = cell;
            int lv = _state.Level(_slots[i].Key);
            t.Text = _labels switch
            {
                Labels.None => "",
                Labels.NameAndLevel => $"{_slots[i].Title} {Strings.Level}{lv}",
                _ => Strings.Level + lv,
            };
            // 名前まで出すと 32px の枠には入らない。**枠を越えて中央に伸ばす**
            t.Width = _labels == Labels.NameAndLevel ? cell * 2 : cell;
            int row = i / perRow;
            double left = (BoardRenderer.RowInset(_slots.Count, row, _boardPixelWidth)
                           + BoardRenderer.SlotLeft(i % perRow)) * _scale;
            Canvas.SetLeft(t, left - (t.Width - cell) / 2);
            Canvas.SetTop(t, BoardRenderer.LabelBandTop(row) * _scale);
        }
    }

    /// <summary>
    /// 稼働状況を動きで表す。**数字は出さない。**
    /// 判定に使う値は経験値と同じものを使う。表示と中身がずれると嘘くさくなる。
    /// </summary>
    private static string MotionFor(double load)
    {
        if (load >= 0.85) return "peek";   // 必死の顔
        if (load >= 0.45) return "walk";   // 動きが速い
        if (load <= 0.03) return "sleep";  // ほぼ何もしていない
        return "idle";
    }

    // -----------------------------------------------------------------------
    // 実測 → 経験値 → 物語
    // -----------------------------------------------------------------------
    private void OnSensorTick()
    {
        _snapshot = _sampler.Sample();
        (_gpus, _throughput) = _pdh.Sample();
        _drives = _disk.Sample();

        double gpu = _gpus.Count == 0 ? 0 : _gpus.Max(g => g.Utilization);
        // 読み書きは 200MB/s で振り切る目盛りにする。
        // 上限を SSD の実力(数GB/s)に合わせると、普段の操作では針が動かず何も伝わらない。
        double diskRate = (_throughput.ReadBytesPerSec + _throughput.WriteBytesPerSec) / (200.0 * 1024 * 1024);
        double fullest = _drives.Count == 0 ? 0 : _drives.Max(d => d.TotalBytes == 0 ? 0 : (double)d.UsedBytes / d.TotalBytes);

        _work = new Work(
            Math.Clamp(_snapshot.CpuTotal, 0, 1),
            Math.Clamp(gpu, 0, 1),
            Math.Clamp(_snapshot.MemoryRatio, 0, 1),
            Math.Clamp(diskRate, 0, 1),
            Math.Clamp(fullest, 0, 1));

        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 10);
        _lastTick = now;

        _slots = Composition.Build(_snapshot, _packages, _gpus, _throughput, _drives,
                                   _disk.Disks, _work.Disk, Strings.Lang);

        _director.Tick(dt, _work, _slots, Strings.Lang);
        UpdateStoryText();

        if ((now - _lastSave).TotalSeconds >= 60) SaveState();

        UpdateDetail();
    }

    private void UpdateStoryText()
    {
        var log = _director.Recent;
        ChapterText.Text = _director.CurrentChapter.Title.For(Strings.Lang);
        StoryLine1.Text = log.Count > 0 ? log[0] : "";
        StoryLine2.Text = log.Count > 1 ? log[1] : "";
    }

    private void SaveState()
    {
        _state.Zoom = _scale;
        _state.WindowLeft = Left;
        _state.WindowTop = Top;
        _state.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _state.Save();
        _lastSave = DateTime.UtcNow;
    }

    /// <summary>
    /// 絵の上を指したとき、それが何かを出す。
    ///
    /// **盤は1枚の絵なので、当たり判定は座標から自分で割り出す。**
    /// 部品ごとに UI 要素を置く手もあるが、そうすると絵と当たり判定が
    /// 二重管理になり、並びを変えたときにずれる。
    /// </summary>
    private void OnBoardHover(object sender, MouseEventArgs e)
    {
        int index = SlotAt(e.GetPosition(BoardHost));
        if (index < 0 || index >= _slots.Count)
        {
            _hoverTip.IsOpen = false;
            return;
        }

        var slot = _slots[index];
        int lv = _state.Level(slot.Key);
        string detail = slot.Detail.Length > 0 ? "\n" + slot.Detail : "";
        _hoverTipText.Text =
            $"{Director.DisplayName(slot.CharacterId, Strings.Lang)}  {Strings.Level}{lv}\n" +
            $"{slot.Title}  {slot.Load * 100:0}%{detail}";

        _hoverTip.PlacementTarget = BoardHost;
        _hoverTip.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        var p = e.GetPosition(BoardHost);
        _hoverTip.HorizontalOffset = p.X + 14;
        _hoverTip.VerticalOffset = p.Y + 18;
        _hoverTip.IsOpen = true;
    }

    /// <summary>盤の中の座標から、何番目の子かを割り出す。外していれば -1。</summary>
    private int SlotAt(Point p)
    {
        if (_slots.Count == 0 || _scale <= 0) return -1;
        double ax = p.X / _scale, ay = p.Y / _scale;

        int row = (int)(ay / BoardRenderer.RowHeight);
        int perRow = BoardRenderer.PerRow(_slots.Count);
        if (row < 0 || row >= BoardRenderer.RowsFor(_slots.Count)) return -1;

        double top = BoardRenderer.RowTop(row) + BoardRenderer.BoardTop;
        // 頭の少し上から、名札の帯までを当たりにする。厳密に体だけだと掴みにくい
        if (ay < top - 3 || ay > top + BoardRenderer.Cell + BoardRenderer.SlotH) return -1;

        double x = ax - BoardRenderer.RowInset(_slots.Count, row, _boardPixelWidth) - BoardRenderer.PadX;
        if (x < 0) return -1;
        int col = (int)(x / (BoardRenderer.Cell + BoardRenderer.Gap));
        if (col >= perRow) return -1;
        // 隙間を指しているときは何も出さない
        if (x - col * (BoardRenderer.Cell + BoardRenderer.Gap) > BoardRenderer.Cell) return -1;

        int index = row * perRow + col;
        return index < _slots.Count ? index : -1;
    }

    /// <summary>
    /// 右クリックの献立。**設定画面は作らない。**
    /// 常駐の玩具に画面をもう一枚足すと、それだけで「アプリ」になってしまう。
    /// 触る項目はこの4つで足りる。
    /// </summary>
    private void ShowMenu()
    {
        var menu = new ContextMenu();

        var top = new MenuItem { Header = Strings.TopmostTip, IsCheckable = true, IsChecked = Topmost };
        top.Click += (_, _) =>
        {
            Topmost = !Topmost;
            _state.Topmost = Topmost;
            TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        };
        menu.Items.Add(top);

        var auto = new MenuItem { Header = Strings.RunAtLogin, IsCheckable = true, IsChecked = Startup.IsEnabled };
        auto.Click += (_, _) => Startup.Set(!Startup.IsEnabled);
        menu.Items.Add(auto);

        var lang = new MenuItem { Header = Strings.Lang == "ja" ? "English" : "日本語" };
        lang.Click += (_, _) =>
        {
            Strings.Lang = Strings.Lang == "ja" ? "en" : "ja";
            _state.Lang = Strings.Lang;
            ApplyLanguage();
        };
        menu.Items.Add(lang);

        var labels = new MenuItem { Header = Strings.LabelMenu };
        foreach (var (mode, header) in new[]
                 {
                     (Labels.LevelOnly, Strings.LabelLevelOnly),
                     (Labels.NameAndLevel, Strings.LabelNameAndLevel),
                     (Labels.None, Strings.LabelNone),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _labels == mode };
            var captured = mode;
            item.Click += (_, _) =>
            {
                _labels = captured;
                _state.Labels = captured switch
                {
                    Labels.NameAndLevel => "name",
                    Labels.None => "none",
                    _ => "level",
                };
                PlaceLevelLabels();
            };
            labels.Items.Add(item);
        }
        menu.Items.Add(labels);

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = Strings.Close };
        quit.Click += (_, _) => Close();
        menu.Items.Add(quit);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private void ApplyLanguage()
    {
        LangButton.Content = Strings.Lang == "ja" ? "JA" : "EN";
        LangButton.ToolTip = Strings.LangTip;
        CloseButton.ToolTip = Strings.Close;
        SizeButton.ToolTip = Strings.SizeTip;
        TopmostButton.ToolTip = Strings.TopmostTip;
        Grip.ToolTip = Strings.ResizeTip;
        ExpandButton.ToolTip = _expanded ? Strings.CollapseTip : Strings.ExpandTip;
        UpdateStoryText();
        PlaceLevelLabels();
        UpdateDetail();
    }

    // -----------------------------------------------------------------------
    // 展開時の数値
    // -----------------------------------------------------------------------
    private void UpdateDetail()
    {
        if (!_expanded) return;
        DetailStack.Children.Clear();

        // CPU は載っている数だけ。2ソケットなら2行出る
        for (int p = 0; p < _packages.Count; p++)
        {
            var mine = _packages[p].Where(i => i < _snapshot.CpuCores.Count)
                                   .Select(i => _snapshot.CpuCores[i]).ToList();
            double load = mine.Count == 0 ? _snapshot.CpuTotal : mine.Average();
            DetailStack.Children.Add(CoreRow(_packages.Count > 1 ? $"CPU{p}" : "CPU", mine,
                $"{load * 100:0}%  {Strings.CoresSuffix(mine.Count)}"));
        }

        DetailStack.Children.Add(BarRow("MEM", _snapshot.MemoryRatio,
            $"{Gb(_snapshot.MemoryUsedBytes):0.0} / {Gb(_snapshot.MemoryTotalBytes):0.0} GB"));

        // GPU は**必ず台数ぶん並べる。**1枚しか出さないと、複数挿しの機械で嘘になる。
        if (_gpus.Count == 0)
        {
            DetailStack.Children.Add(BarRow("GPU", 0, Strings.NoGpu));
        }
        else
        {
            for (int i = 0; i < _gpus.Count; i++)
            {
                var g = _gpus[i];
                string label = _gpus.Count == 1 ? "GPU" : $"GPU{i}";
                DetailStack.Children.Add(BarRow(label, g.Utilization, $"{g.Utilization * 100:0}%   {g.Name}"));
                if (g.VramTotalBytes > 0)
                    DetailStack.Children.Add(BarRow(Strings.Vram,
                        (double)g.VramUsedBytes / g.VramTotalBytes,
                        $"{Gb(g.VramUsedBytes):0.0} / {Gb(g.VramTotalBytes):0.0} GB"));
                else if (g.VramUsedBytes > 0)
                    DetailStack.Children.Add(BarRow(Strings.Vram, 0, $"{Gb(g.VramUsedBytes):0.0} GB"));
            }
        }

        // 記憶装置は**種類ごとに**出す。総量ひとつだと、どれが動いているのか分からない
        foreach (var slot in _slots.Where(s2 => s2.Key is "m2" or "ssd" or "hdd"))
        {
            var mine = _drives.Where(d => KindOf(d) == slot.Key).Select(d => d.DiskNumber).ToHashSet();
            DetailStack.Children.Add(BarRow(slot.Title, slot.Load,
                $"{Mb(_throughput.For(mine)):0.0} MB/s"));
        }
        DetailStack.Children.Add(BarRow("I/O", Math.Clamp(_work.Disk, 0, 1),
            $"{Mb(_throughput.ReadBytesPerSec):0.0} / {Mb(_throughput.WriteBytesPerSec):0.0} MB/s"));

        foreach (var d in _drives)
        {
            double ratio = d.TotalBytes == 0 ? 0 : (double)d.UsedBytes / d.TotalBytes;
            string kind = d.IsNvme ? Strings.M2 : d.IsSsd ? Strings.Ssd : Strings.Hdd;
            string name = d.Label.Length > 0 ? $"{d.Name} {d.Label}" : d.Name;
            DetailStack.Children.Add(BarRow(name, ratio,
                $"{Gb(d.UsedBytes):0} / {Gb(d.TotalBytes):0} GB  {kind}"));
        }

        DetailStack.Children.Add(new TextBlock
        {
            Text = Strings.Disclaimer,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7A, 0x93)),
        });
    }

    /// <summary>そのドライブがどの子のものか。Composition の振り分けと必ず揃えること。</summary>
    private static string KindOf(DriveInfoLite d) => d.IsNvme ? "m2" : d.IsSsd ? "ssd" : "hdd";

    private static double Gb(ulong bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
    private static double Mb(double bytes) => bytes / 1024.0 / 1024.0;

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
            VerticalAlignment = VerticalAlignment.Center,
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock
        {
            Text = label, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xC4)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(l, 0);
        grid.Children.Add(l);

        var t = new TextBlock
        {
            Text = text, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            // **切れたら「…」を出す。**
            // 黙って切れると「RTX 3080 Ti」が「RTX 3080」と読めてしまう
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text,
        };
        Grid.SetColumn(t, 2);
        grid.Children.Add(t);
        return grid;
    }
}
