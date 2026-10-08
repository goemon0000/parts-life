using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PartsLife.Checks;

namespace PartsLife.CheckWindow;

/// <summary>
/// 窓の部品。本体の窓と同じ色（暗い地に薄緑）で揃える。
/// XAML を持たないのは、このアセンブリを「開いたときだけ読む」軽い物にしておくため。
/// </summary>
internal static class Ui
{
    public static readonly Brush Back = Frozen(0x10, 0x13, 0x19);
    public static readonly Brush Panel = Frozen(0x18, 0x1C, 0x25);
    public static readonly Brush Edge = Frozen(0x2A, 0x33, 0x44);
    public static readonly Brush Text = Frozen(0xDD, 0xE5, 0xF8);
    public static readonly Brush Sub = Frozen(0x9A, 0xA7, 0xC4);
    public static readonly Brush Dim = Frozen(0x6E, 0x7A, 0x93);
    public static readonly Brush Accent = Frozen(0x5D, 0xD8, 0xBB);
    public static readonly Brush Yellow = Frozen(0xE7, 0xC8, 0x4B);
    public static readonly Brush Red = Frozen(0xF0, 0x50, 0x70);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var s = new SolidColorBrush(Color.FromRgb(r, g, b));
        s.Freeze();
        return s;
    }

    public static Brush VerdictBrush(Verdict v) => v switch
    {
        Verdict.Ok => Accent,
        Verdict.Caution => Yellow,
        Verdict.Check => Red,
        _ => Dim,
    };

    public static TextBlock Label(string text, double size = 12, Brush? color = null, bool bold = false, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? Text,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 12, 0, 6) };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = primary ? Back : Text,
            Background = primary ? Accent : Panel,
            BorderBrush = primary ? Accent : Edge,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
        };
        b.Template = FlatTemplate();
        return b;
    }

    private static ControlTemplate? _flat;
    /// <summary>平らなボタン。既定の見た目は暗い地に浮くので、枠と地だけの形にする。</summary>
    private static ControlTemplate FlatTemplate()
    {
        if (_flat is not null) return _flat;
        var t = new ControlTemplate(typeof(ButtonBase));
        var border = new FrameworkElementFactory(typeof(Border), "b");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter), "cp");
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
        // 選ばれている物は薄緑。**部品の側（b / cp）に当てる。** 押しボタン自身の Background は
        // 作るときに直接入れてあり、直接入れた値は型紙の Trigger より強いので、そちらに当てても変わらない
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "b"));
        on.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "b"));
        on.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, Back, "cp"));
        t.Triggers.Add(hover);
        t.Triggers.Add(off);
        t.Triggers.Add(on);
        t.Seal();
        return _flat = t;
    }

    /// <summary>
    /// 択一の並び。ComboBox は暗い地で見た目が崩れる（吹き出しが白い）ので、押しボタンを並べる。
    /// </summary>
    public sealed class Choice<T> : WrapPanel
    {
        private readonly List<(ToggleButton Button, T Value)> _items = new();
        public event Action<T>? Changed;
        public T Value { get; private set; }

        public Choice(IEnumerable<(string Text, T Value)> items, T initial)
        {
            Orientation = Orientation.Horizontal;
            Value = initial;
            foreach (var (text, value) in items)
            {
                var b = new ToggleButton
                {
                    Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 4),
                    Foreground = Text, Background = Panel, BorderBrush = Edge, BorderThickness = new Thickness(1),
                    Template = FlatTemplate(), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
                    IsChecked = EqualityComparer<T>.Default.Equals(value, initial),
                };
                var captured = value;
                b.Click += (_, _) => Select(captured);
                _items.Add((b, value));
                Children.Add(b);
            }
        }

        public void Select(T value)
        {
            Value = value;
            foreach (var (b, v) in _items) b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            Changed?.Invoke(value);
        }

        public void SetEnabled(T value, bool enabled, string? tip = null)
        {
            foreach (var (b, v) in _items)
                if (EqualityComparer<T>.Default.Equals(v, value)) { b.IsEnabled = enabled; b.ToolTip = tip; }
        }
    }

    /// <summary>判定1行: 色の札・題・理由。</summary>
    public static UIElement FindingRow(Finding f)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Background = VerdictBrush(f.Verdict),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = L.VerdictLabel(f.Verdict), FontSize = 11, Foreground = Back, FontWeight = FontWeights.SemiBold },
        };
        grid.Children.Add(badge);

        var title = Label(f.Title, 12, Text, wrap: false);
        title.ToolTip = f.Title;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var reason = Label(f.Reason, 12, f.Verdict == Verdict.Unavailable ? Dim : Sub);
        Grid.SetColumn(reason, 2);
        grid.Children.Add(reason);
        return grid;
    }

    public static Border Card(UIElement child) => new()
    {
        Background = Panel,
        BorderBrush = Edge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 8, 12, 10),
        Margin = new Thickness(0, 0, 0, 10),
        Child = child,
    };

    public static ProgressBar Progress() => new()
    {
        Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 4),
        Foreground = Accent, Background = Edge, BorderThickness = new Thickness(0),
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>「見出し ＋ 択一」の1行。択一の側は幅いっぱいまで使い、入りきらなければ折り返す。</summary>
    public static Grid FieldRow(string label, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 12, Foreground = Sub, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
        g.Children.Add(l);
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    /// <summary>結果を文字にする（クリップボードへ写すため）。</summary>
    public static string ToText(IEnumerable<Finding> findings) =>
        string.Join(Environment.NewLine, findings.Select(f => $"[{L.VerdictLabel(f.Verdict)}] {f.Title.Trim()}: {f.Reason}"));
}
