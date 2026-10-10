using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PartsLife.Checks;
using PartsLife.Checks.Build;
using PartsLife.Checks.SystemInfo;

namespace PartsLife.CheckWindow;

/// <summary>窓の中の1面。閉じるときに止められること。</summary>
internal abstract class CheckPanel : StackPanel
{
    /// <summary>動いている物を全部止める。何度呼んでもよい。</summary>
    public abstract void Stop();
    /// <summary>止め終わるのを待つための仕事（窓を閉じた後の片付けで待つ）。</summary>
    public abstract Task Running { get; }

    /// <summary>
    /// 書き出し（--shot-check）の前に、その面が見せる物を用意する。既定では何もしない（押す前の面のまま）。
    /// **管理者の要る物・負荷をかける物は、ここで走らせない。**
    /// </summary>
    public virtual Task PrepareShotAsync() => Task.CompletedTask;
}

/// <summary>
/// システムチェック。管理者なし、数秒で終わる一覧。
/// 下に「構成コード」（部品の型番を web-autoai.com のマイPCに貼るための文字列）の欄も持つ。**送信はしない。**
/// </summary>
internal sealed class SystemPanel : CheckPanel
{
    private readonly Button _run;
    private readonly Button _copy;
    private readonly TextBlock _status;
    private readonly StackPanel _results = new();
    private CancellationTokenSource? _cts;
    private Task _task = Task.CompletedTask;
    private List<SystemItem> _last = new();

    // 構成コード
    private readonly Button _buildCopy;
    private readonly Button _buildSave;
    private readonly Button _buildShow;
    private readonly TextBlock _buildStatus;
    private readonly StackPanel _buildPreview = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _buildCodeBox = PreviewBox(80);
    private readonly TextBox _buildJsonBox = PreviewBox(260);
    /// <summary>窓を開いている間は一度だけ読む（部品は窓を開いたまま変わらない）。失敗したら次に押したとき読み直す。</summary>
    private Task<BuildCodeResult>? _build;

    public override Task Running => Task.WhenAll(_task, _build ?? Task.CompletedTask);

    public SystemPanel()
    {
        Children.Add(Ui.Label(T.SystemIntro, 12, Ui.Sub));
        _run = Ui.Button(T.SystemRun, primary: true);
        _copy = Ui.Button(T.Copy);
        _copy.IsEnabled = false;
        _status = Ui.Label("", 12, Ui.Sub);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Ui.Row(_run, _copy, _status));
        Children.Add(_results);
        Children.Add(Ui.Label(T.SystemNotMeasured, 11, Ui.Dim));

        _buildCopy = Ui.Button(T.BuildCopy, primary: true);
        _buildSave = Ui.Button(T.BuildSave);
        _buildShow = Ui.Button(T.BuildShow);
        _buildStatus = Ui.Label("", 12, Ui.Sub);
        _buildStatus.Margin = new Thickness(0, 2, 0, 0);
        _buildPreview.Children.Add(Ui.Label(T.BuildCodeLabel, 11, Ui.Dim));
        _buildPreview.Children.Add(_buildCodeBox);
        _buildPreview.Children.Add(Ui.Label(T.BuildJsonLabel, 11, Ui.Dim));
        _buildPreview.Children.Add(_buildJsonBox);
        var buildBox = new StackPanel();
        buildBox.Children.Add(new TextBlock { Text = T.BuildHeading, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Accent, Margin = new Thickness(0, 0, 0, 4) });
        buildBox.Children.Add(Ui.Label(T.BuildIntro, 12, Ui.Sub));
        buildBox.Children.Add(Ui.Row(_buildCopy, _buildSave, _buildShow));
        buildBox.Children.Add(_buildStatus);
        buildBox.Children.Add(_buildPreview);
        var buildCard = Ui.Card(buildBox);
        buildCard.Margin = new Thickness(0, 12, 0, 10);
        Children.Add(buildCard);

        _buildCopy.Click += async (_, _) => await CopyBuildAsync();
        _buildSave.Click += async (_, _) => await SaveBuildAsync();
        _buildShow.Click += async (_, _) => await ToggleBuildPreviewAsync();

        _run.Click += async (_, _) => await RunAsync();
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(Ui.ToText(_last.Select(i => i.Finding with { Title = $"{i.Group} / {i.Finding.Title}" }))); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
    }

    private async Task RunAsync()
    {
        _run.IsEnabled = false;
        _copy.IsEnabled = false;
        _status.Text = T.Working;
        _results.Children.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var work = Task.Run(() => SystemCheck.Run(ct), ct);
            _task = work;
            _last = await work;
            Show(_last);
            _status.Text = T.Summary(_last.Select(i => i.Finding));
            _copy.IsEnabled = true;
        }
        catch (OperationCanceledException) { _status.Text = T.Stopped; }
        catch (Exception ex) { _status.Text = T.Failed + ex.Message; }
        finally { _run.IsEnabled = true; }
    }

    private void Show(IEnumerable<SystemItem> items)
    {
        foreach (var group in items.GroupBy(i => i.Group))
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = group.Key, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Accent, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var i in group) box.Children.Add(Ui.FindingRow(i.Finding));
            _results.Children.Add(Ui.Card(box));
        }
    }

    // =====================================================================
    // 構成コード
    // =====================================================================

    /// <summary>部品を読む（初回だけ。WMI は遅いので別のスレッドで）。読めなければ null を返し、理由を出す。</summary>
    private async Task<BuildCodeResult?> BuildAsync()
    {
        SetBuildEnabled(false);
        if (_build is null || _build.IsFaulted) _buildStatus.Text = T.BuildReading;
        try
        {
            _build ??= Task.Run(() => BuildCode.Make(BuildCollector.Collect()));
            var r = await _build;
            if (r.Config.IsEmpty)
            {
                _build = null;
                _buildStatus.Text = T.BuildNothing;
                return null;
            }
            _buildCodeBox.Text = r.Code;
            _buildJsonBox.Text = r.Json;
            return r;
        }
        catch (Exception ex)
        {
            _build = null;
            _buildStatus.Text = T.Failed + ex.Message;
            return null;
        }
        finally { SetBuildEnabled(true); }
    }

    private async Task CopyBuildAsync()
    {
        if (await BuildAsync() is not { } r) return;
        try
        {
            Clipboard.SetText(r.Code);
            _buildStatus.Text = WithNotes(T.BuildCopied(T.BuildSummary(r.Config)), r);
        }
        catch (System.Runtime.InteropServices.COMException) { _buildStatus.Text = T.BuildClipboardFailed; }
    }

    private async Task SaveBuildAsync()
    {
        if (await BuildAsync() is not { } r) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = T.BuildSaveTitle,
            FileName = "parts-life-build.json",
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            // UTF-8（BOM なし）。受け手の JSON.parse は BOM を嫌う
            File.WriteAllText(dlg.FileName, r.Json + "\n", new UTF8Encoding(false));
            _buildStatus.Text = WithNotes(T.BuildSaved(Path.GetFileName(dlg.FileName)), r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _buildStatus.Text = T.Failed + ex.Message; }
    }

    private async Task ToggleBuildPreviewAsync()
    {
        if (_buildPreview.Visibility == Visibility.Visible)
        {
            _buildPreview.Visibility = Visibility.Collapsed;
            _buildShow.Content = T.BuildShow;
            return;
        }
        if (await BuildAsync() is not { } r) return;
        _buildPreview.Visibility = Visibility.Visible;
        _buildShow.Content = T.BuildHide;
        if (_buildStatus.Text == T.BuildReading) _buildStatus.Text = WithNotes(T.BuildSummary(r.Config), r);
    }

    private static string WithNotes(string text, BuildCodeResult r) =>
        r.Notes.Count == 0 ? text : text + Environment.NewLine + string.Join(Environment.NewLine, r.Notes);

    private void SetBuildEnabled(bool on) => _buildCopy.IsEnabled = _buildSave.IsEnabled = _buildShow.IsEnabled = on;

    /// <summary>読むだけの欄（選んで写せる）。暗い地に合わせた色、等幅。</summary>
    private static TextBox PreviewBox(double maxHeight) => new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = maxHeight,
        Margin = new Thickness(0, 2, 0, 6),
        Padding = new Thickness(6, 4, 6, 4),
        FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
        FontSize = 11,
        Foreground = Ui.Text,
        Background = Ui.Back,
        BorderBrush = Ui.Edge,
        CaretBrush = Ui.Text,
    };

    /// <summary>書き出しでは、管理者なしのシステムチェックを本当に走らせて、その結果を描く。</summary>
    public override Task PrepareShotAsync() => RunAsync();

    public override void Stop() => _cts?.Cancel();
}
