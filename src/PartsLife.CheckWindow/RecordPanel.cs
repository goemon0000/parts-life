using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PartsLife.Checks;
using PartsLife.Checks.Build;
using PartsLife.Checks.Recording;

namespace PartsLife.CheckWindow;

/// <summary>
/// テストの記録。ブラウザのベンチマークやストレステストの間、センサーを 1 秒ごとにファイルへ記録し、止めたら手元で要約する。
/// 要約は「AIに渡す用」の文書にして、中身を確かめてから保存・コピーできる。
///
/// - **押したときだけ動く。** 開いただけでは何も読まない（「読めるセンサーを確認」か「開始」を押したときだけ）
/// - 管理者もドライバも要らない。通信はしない。書くのは ドキュメント\PartsLife\runs の記録ファイルと、利用者が選んだ保存先だけ
/// - この窓を閉じると記録は止まり、ここまでの分が保存される（<see cref="Stop"/>）
/// - --shot-check のときは、一時フォルダに 6 秒だけ本当に記録し、終わったら消す（ドキュメントには書かない）
/// </summary>
internal sealed class RecordPanel : CheckPanel
{
    private enum Phase { Idle, Preparing, Recording, Stopping }

    private readonly string _lang = L.Lang;
    private readonly string _folder;
    private readonly bool _shot = Ui.ShotMode;
    private Phase _phase = Phase.Idle;
    private bool _closed;

    private readonly Button _check, _start, _stop, _openRun, _openFolder;
    private readonly TextBlock _status;
    private readonly StackPanel _caps = new();
    private readonly StackPanel _marksCard = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _marksRow = new();
    private readonly TextBlock _marksList = Ui.Label("", 12, Ui.Sub);
    private readonly StackPanel _liveCard = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _live = Ui.Label("", 12, Ui.Text);
    private readonly StackPanel _result = new();
    private readonly StackPanel _ai = new();
    private readonly List<Button> _markButtons = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private RunRecorder? _rec;
    private volatile LiveSample? _latest;
    private IReadOnlyList<GpuDescriptor> _gpus = Array.Empty<GpuDescriptor>();
    private int _rows;

    /// <summary>いま画面に出している記録（AI に渡す用を作るのに使う）。</summary>
    private (RunSession Session, RunSummary Summary, string CsvPath)? _view;
    private string? _aiText;
    private Task _task = Task.CompletedTask;
    public override Task Running => _task;

    public RecordPanel()
    {
        _folder = _shot ? Path.Combine(Path.GetTempPath(), "pl-shot-record-" + Guid.NewGuid().ToString("N")) : RunStore.DefaultFolder();

        Children.Add(Ui.Label(T.RecordIntro, 12, Ui.Sub));
        var how = Ui.Label(T.RecordHow, 12, Ui.Sub);
        how.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(how);
        var folderNote = Ui.Label(T.RecordFolderNote(Shown(_folder)), 11, Ui.Dim);
        folderNote.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(folderNote);

        _start = Ui.Button(T.RecordStart, primary: true);
        _stop = Ui.Button(T.RecordStop);
        _stop.IsEnabled = false;
        _check = Ui.Button(T.RecordCheck);
        _openRun = Ui.Button(T.RecordOpenRun);
        _openFolder = Ui.Button(T.RecordOpenFolder);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _start, _stop, _check, _openRun, _openFolder }) { b.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(b); }
        Children.Add(buttons);
        _status = Ui.Label("", 12, Ui.Sub);
        Children.Add(_status);

        Children.Add(_liveCard);
        _liveCard.Children.Add(Ui.Card(Stack(Ui.Heading(T.LiveHeading, first: true), _live)));
        BuildMarks();
        Children.Add(_marksCard);
        Children.Add(_caps);
        Children.Add(_result);
        Children.Add(_ai);

        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await EndRecordingAsync("user");
        _check.Click += async (_, _) => await CheckAsync();
        _openRun.Click += async (_, _) => await OpenPastAsync();
        _openFolder.Click += (_, _) => OpenFolder();
        _timer.Tick += (_, _) => Tick();
        SetPhase(Phase.Idle);
    }

    // =====================================================================
    // 状態
    // =====================================================================

    private void SetPhase(Phase p)
    {
        _phase = p;
        bool idle = p == Phase.Idle;
        _start.IsEnabled = _check.IsEnabled = _openRun.IsEnabled = idle;
        _stop.IsEnabled = p == Phase.Recording;
        foreach (var b in _markButtons) b.IsEnabled = p == Phase.Recording;
    }

    private RunOptions Options() => new() { Folder = _folder, AppVersion = AppVersionText() };

    private static string AppVersionText()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>画面に出す保存先。書き出し（--shot-check）の間は、一時フォルダでなく本来の場所の形で見せる（個人のパスを写さない）。</summary>
    private string Shown(string folder) => _shot ? Path.Combine(L.T("ドキュメント", "Documents"), "PartsLife", "runs") : folder;

    // =====================================================================
    // 読めるセンサーの確認
    // =====================================================================

    private async Task CheckAsync()
    {
        SetPhase(Phase.Preparing);
        _status.Text = T.RecordPreparing;
        try
        {
            var report = await Task.Run(() => RunRecorder.Probe(Options()));
            if (_closed) return;
            ShowCapabilities(report);
            _status.Text = T.RecordChecked(report.Count(Availability.Available), report.Count(Availability.NeedsExtension), report.Count(Availability.Unavailable));
        }
        catch (Exception ex) { if (!_closed) _status.Text = T.Failed + ex.Message; }
        finally { if (!_closed) SetPhase(Phase.Idle); }
    }

    private void ShowCapabilities(CapabilityReport report)
    {
        _caps.Children.Clear();
        var box = new StackPanel();
        box.Children.Add(Ui.Heading(T.CapHeading, first: true));
        box.Children.Add(Ui.Label(T.CapNote, 11, Ui.Dim));

        var rows = report.Display();
        bool IsExt(MetricStatus m) => m.State == Availability.NeedsExtension;
        AddGroup(box, T.GroupCpu, rows.Where(m => m.Key.StartsWith("cpu.", StringComparison.Ordinal) && !IsExt(m)));
        AddGroup(box, T.GroupSystem, rows.Where(m => !IsExt(m) && !m.Key.StartsWith("cpu.", StringComparison.Ordinal)
                                                     && !m.Key.StartsWith("gpu", StringComparison.Ordinal)));
        foreach (var g in report.Gpus)
            AddGroup(box, T.GpuGroup(g), rows.Where(m => !IsExt(m) && m.Key.StartsWith(g.Id + ".", StringComparison.Ordinal)));
        AddGroup(box, T.GroupExtension, rows.Where(IsExt));
        _caps.Children.Add(Ui.Card(box));
    }

    private void AddGroup(StackPanel parent, string title, IEnumerable<MetricStatus> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        parent.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, Margin = new Thickness(0, 10, 0, 3),
            TextWrapping = TextWrapping.Wrap,
        });
        var ok = list.Where(m => m.IsAvailable).Select(m => Short(m.Key)).ToList();
        var line = Ui.Label(T.CapReadable + ": " + (ok.Count > 0 ? string.Join(L.T("・", ", "), ok) : T.CapNone), 12, Ui.Sub);
        parent.Children.Add(line);
        foreach (var m in list.Where(m => !m.IsAvailable))
            parent.Children.Add(CapabilityRow(m));
    }

    /// <summary>グループの中では GPU の番号を省く（見出しに出ているため）。</summary>
    private string Short(string key)
    {
        string label = RecordText.Label(key, _lang);
        if (MetricKeys.TryParseGpu(key, out int gi, out _))
        {
            string prefix = "GPU" + gi.ToString(CultureInfo.InvariantCulture) + " ";
            if (label.StartsWith(prefix, StringComparison.Ordinal)) label = label[prefix.Length..];
        }
        return label;
    }

    private UIElement CapabilityRow(MetricStatus m)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Badge(RecordText.State(m.State, _lang), m.State == Availability.NeedsExtension ? Ui.Yellow : Ui.Dim));
        var name = Ui.Label(Short(m.Key), 12, Ui.Text, wrap: false);
        name.ToolTip = m.Key;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var reason = Ui.Label(RecordText.Reason(m.Reason, _lang), 12, Ui.Dim);
        Grid.SetColumn(reason, 2);
        grid.Children.Add(reason);
        return grid;
    }

    private static Border Badge(string text, Brush color) => new()
    {
        Background = color, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Ui.Back, FontWeight = FontWeights.SemiBold },
    };

    // =====================================================================
    // 記録
    // =====================================================================

    private async Task StartAsync()
    {
        SetPhase(Phase.Preparing);
        _status.Text = T.RecordPreparing;
        _result.Children.Clear();
        _ai.Children.Clear();
        _view = null;
        _aiText = null;
        try
        {
            // センサーの確認と部品の型番の読み取り（WMI。数秒かかる）を、始める前に済ませる。
            // 部品が読めなくても記録は始める（構成は空のまま残す）
            var rec = await Task.Run(() =>
            {
                var r = RunRecorder.Prepare(Options());
                try
                {
                    r.Start(CollectBuild());
                    return r;
                }
                catch
                {
                    r.Dispose();
                    throw;
                }
            });
            if (_closed)
            {
                _task = rec.StopAsync("window-closed");
                return;
            }
            _rec = rec;
            _rows = 0;
            _latest = null;
            _gpus = rec.Capabilities.Gpus;
            ShowCapabilities(rec.Capabilities);
            _marksList.Text = "";
            _marksCard.Visibility = _liveCard.Visibility = Visibility.Visible;
            rec.Sampled += s => { _latest = s; };
            rec.Ended += reason => Dispatcher.InvokeAsync(() => EndRecordingAsync(reason));
            SetPhase(Phase.Recording);
            Tick();
            _timer.Start();
        }
        catch (RecordException ex)
        {
            if (_closed) return;
            _status.Text = T.RecordStartFailed(ex);
            SetPhase(Phase.Idle);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _status.Text = T.Failed + ex.Message;
            SetPhase(Phase.Idle);
        }
    }

    /// <summary>この PC の部品の構成（PCB1 と同じ中身）。読めなければ null（記録は続ける）。</summary>
    private static BuildSnapshot? CollectBuild()
    {
        try
        {
            var r = BuildCode.Make(BuildCollector.Collect());
            return r.Config.IsEmpty ? null : new BuildSnapshot(r.Code, r.Json);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("PartsLife: record: could not read the build: " + ex.GetType().Name);
            return null;
        }
    }

    private void Tick()
    {
        if (_rec is null) return;
        var s = _latest;
        var t = TimeSpan.FromSeconds(s?.TSec ?? 0);
        _rows = s is null ? 0 : (int)Math.Round(s.TSec);   // 1 秒ごと
        _status.Text = T.RecordRunning(t, _rows);
        _live.Text = s is null ? "…" : LiveText(s);
    }

    private string LiveText(LiveSample s)
    {
        string V(string key, string fmt, string unit)
        {
            double v = s.Get(key);
            return double.IsNaN(v) ? "-" : v.ToString(fmt, CultureInfo.InvariantCulture) + unit;
        }
        var sb = new StringBuilder();
        sb.Append("CPU   ")
          .Append(T.Load).Append(' ').Append(V(MetricKeys.CpuLoad, "0", "%")).Append("   ")
          .Append(T.Clock).Append(' ').Append(V(MetricKeys.CpuClock, "0", " MHz"));
        if (!double.IsNaN(s.Get(MetricKeys.CpuPackagePower))) sb.Append("   ").Append(L.T("電力", "power")).Append(' ').Append(V(MetricKeys.CpuPackagePower, "0.0", " W"));
        foreach (var g in _gpus)
        {
            sb.Append('\n').Append("GPU").Append(g.Index).Append("  ")
              .Append(T.Load).Append(' ').Append(V(MetricKeys.Gpu(g.Index, MetricKeys.Util), "0", "%"));
            if (!double.IsNaN(s.Get(MetricKeys.Gpu(g.Index, MetricKeys.Temp))))
                sb.Append("   ").Append(T.Temperature).Append(' ').Append(V(MetricKeys.Gpu(g.Index, MetricKeys.Temp), "0", " °C"));
            if (!double.IsNaN(s.Get(MetricKeys.Gpu(g.Index, MetricKeys.Clock))))
                sb.Append("   ").Append(T.Clock).Append(' ').Append(V(MetricKeys.Gpu(g.Index, MetricKeys.Clock), "0", " MHz"));
            if (!double.IsNaN(s.Get(MetricKeys.Gpu(g.Index, MetricKeys.Power))))
                sb.Append("   ").Append(L.T("電力", "power")).Append(' ').Append(V(MetricKeys.Gpu(g.Index, MetricKeys.Power), "0", " W"));
        }
        return sb.ToString();
    }

    private void BuildMarks()
    {
        _marksCard.Children.Add(Ui.Card(Stack(Ui.Heading(T.MarksHeading, first: true), Ui.Label(T.MarksHelp, 11, Ui.Dim), _marksRow, _marksList)));
        foreach (var kind in new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd, MarkerKind.StressStart, MarkerKind.StressEnd, MarkerKind.Note })
        {
            var b = Ui.Button(RecordText.Marker(kind, _lang));
            b.Margin = new Thickness(0, 6, 8, 0);
            var k = kind;
            b.Click += (_, _) => AddMark(k);
            _markButtons.Add(b);
            _marksRow.Children.Add(b);
        }
    }

    private void AddMark(MarkerKind kind)
    {
        if (_rec is not { IsRecording: true } rec) return;
        try
        {
            var m = rec.AddMarker(kind);
            string line = T.MarkAdded(RecordText.Marker(kind, _lang), TimeSpan.FromSeconds(m.TSec));
            _marksList.Text = _marksList.Text.Length == 0 ? line : _marksList.Text + "\n" + line;
        }
        catch (RecordException) { /* 止まった直後に押された。無視する */ }
    }

    private async Task EndRecordingAsync(string reason)
    {
        if (_phase != Phase.Recording || _rec is not { } rec) return;
        SetPhase(Phase.Stopping);
        _timer.Stop();
        _status.Text = T.RecordStopping;
        try
        {
            var result = await rec.StopAsync(reason);
            _task = Task.CompletedTask;
            rec.Dispose();
            _rec = null;
            if (_closed) return;
            _liveCard.Visibility = Visibility.Collapsed;
            _status.Text = T.RecordDone(result.Session.StopReason);
            ShowResult(result.Session, result.Summary, result.CsvPath, fromFile: false, result.SummaryError);
        }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = T.Failed + ex.Message;
        }
        finally { if (!_closed) SetPhase(Phase.Idle); }
    }

    // =====================================================================
    // 過去の記録
    // =====================================================================

    private async Task OpenPastAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = T.RecordOpenRunTitle,
            Filter = "run-*.json|run-*.json|JSON (*.json)|*.json",
            InitialDirectory = Directory.Exists(_folder) ? _folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        SetPhase(Phase.Preparing);
        _status.Text = T.RecordLoading;
        try
        {
            string path = dlg.FileName;
            var (session, _, summary) = await Task.Run(() => RunStore.Load(path));
            if (_closed) return;
            _result.Children.Clear();
            _ai.Children.Clear();
            _caps.Children.Clear();
            string csv = RunStore.CsvPath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", session.RunId);
            _status.Text = "";
            ShowResult(session, summary, csv, fromFile: true, null);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            if (!_closed) _status.Text = T.Failed + ex.Message;
        }
        finally { if (!_closed) SetPhase(Phase.Idle); }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _status.Text = T.Failed + ex.Message;
        }
    }

    // =====================================================================
    // 要約の表示
    // =====================================================================

    private void ShowResult(RunSession session, RunSummary? summary, string csvPath, bool fromFile, string? error)
    {
        _result.Children.Clear();
        _ai.Children.Clear();
        _aiText = null;
        _view = null;
        var box = new StackPanel();
        box.Children.Add(Ui.Heading(T.ResultHeading + (fromFile ? " " + T.ResultFromFile : ""), first: true));
        if (summary is null)
        {
            box.Children.Add(Ui.Label(T.SummaryFailed(error ?? "?"), 12, Ui.Yellow));
            _result.Children.Add(Ui.Card(box));
            return;
        }
        _view = (session, summary, csvPath);
        box.Children.Add(Ui.Label(T.ResultNote, 11, Ui.Dim));
        box.Children.Add(Ui.Label(T.ResultFacts(summary), 12, Ui.Sub));

        var idle = summary.Notes.Where(n => n.Code == "device-idle").Select(n => n.Subject).ToHashSet(StringComparer.Ordinal);
        AddDevice(box, "CPU", new[] { MetricKeys.CpuLoad, MetricKeys.CpuClock, MetricKeys.CpuPackagePower }, summary, false);
        foreach (var g in session.Gpus.OrderBy(g => g.Index))
        {
            var keys = new[] { MetricKeys.Util, MetricKeys.Temp, MetricKeys.Clock, MetricKeys.Power }.Select(m => MetricKeys.Gpu(g.Index, m)).ToArray();
            AddDevice(box, T.GpuGroup(g), keys, summary, idle.Contains(g.Id));
        }
        AddDevice(box, T.GroupSystem, new[] { MetricKeys.MemUsedPct, MetricKeys.DiskRead, MetricKeys.DiskWrite }, summary, false);

        if (summary.PerCore is { } pc) box.Children.Add(Muted(T.PerCoreLine(pc)));
        box.Children.Add(Muted(summary.SustainedWindowSec > 0 ? T.SustainedHelp(summary.SustainedWindowSec) : T.SustainedNone));

        // 制限の印
        box.Children.Add(Ui.Heading(T.LimitsHeading));
        foreach (var l in summary.Limits)
        {
            string dev = l.Device == "cpu" ? "CPU" : "GPU" + l.Device[3..];
            string text = l.Reasons.Count == 0 ? T.LimitsNone : string.Join("\n", l.Reasons.Select(r => T.LimitLine(r.Name, r.Seconds, r.SecondsWhileBusy)));
            box.Children.Add(Ui.Label(dev + ": " + (l.Reasons.Count == 0 ? text : "\n" + text), 12, Ui.Sub));
        }
        if (summary.Limits.Count == 0) box.Children.Add(Ui.Label(T.LimitsNone, 12, Ui.Dim));

        // 同時に起きた時間帯
        box.Children.Add(Ui.Heading(T.CoHeading));
        if (summary.CoOccurrences.Count == 0) box.Children.Add(Ui.Label(T.CoNone, 12, Ui.Dim));
        foreach (var c in summary.CoOccurrences) box.Children.Add(Ui.Label("・" + RecordText.CoOccurrence(c, _lang), 12, Ui.Sub));

        // 目印の区間
        if (summary.Spans.Count > 0)
        {
            box.Children.Add(Ui.Heading(T.SpansHeading));
            foreach (var sp in summary.Spans) box.Children.Add(Ui.Label("・" + SpanLine(sp), 12, Ui.Sub));
        }

        // 注記（読めなかったこと・記録の傷）
        // 使われなかった GPU は表に書いてある。読めなかった項目は、読めるセンサーの欄に出ているときは重ねない（過去の記録を開いたときは、その欄が無いので出す）
        var notes = summary.Notes.Where(n => n.Code != "device-idle" && (fromFile || n.Code != "unavailable")).ToList();
        if (notes.Count > 0)
        {
            box.Children.Add(Ui.Heading(T.NotesHeading));
            foreach (var n in notes) box.Children.Add(Ui.Label("・" + RecordText.Note(n, _lang), 12, n.Code == "unavailable" ? Ui.Dim : Ui.Sub));
        }

        // 保存した記録とボタン
        box.Children.Add(Ui.Heading(T.FilesHeading));
        string folder = Path.GetDirectoryName(csvPath) ?? _folder;
        box.Children.Add(Ui.Label(Shown(folder) + "\n" + $"run-{session.RunId}.json\nsensors-{session.RunId}.csv", 11, Ui.Dim));
        var ai = Ui.Button(T.AiBuild, primary: true);
        ai.Click += async (_, _) => await BuildAiAsync();
        var row = Ui.Row(ai);
        row.Margin = new Thickness(0, 10, 0, 0);
        box.Children.Add(row);
        _result.Children.Add(Ui.Card(box));
    }

    private void AddDevice(StackPanel parent, string title, IReadOnlyList<string> keys, RunSummary sum, bool idle)
    {
        var rows = keys.Select(k => sum.Metric(k)).Where(m => m is not null).Select(m => m!).ToList();
        if (rows.Count == 0) return;
        var head = new TextBlock { Margin = new Thickness(0, 12, 0, 3), TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, FontSize = 12 });
        if (idle) head.Inlines.Add(new System.Windows.Documents.Run("  " + T.MostlyIdle) { Foreground = Ui.Dim, FontSize = 11 });
        parent.Children.Add(head);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        void Cell(int row, int col, string text, Brush color, bool right)
        {
            var t = new TextBlock { Text = text, FontSize = 12, Foreground = color, Margin = new Thickness(0, 1, 0, 1), TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        Cell(0, 1, T.ColMax, Ui.Dim, true);
        Cell(0, 2, T.ColAvg, Ui.Dim, true);
        Cell(0, 3, T.ColSustained, Ui.Dim, true);
        int r = 1;
        foreach (var m in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var dim = idle ? Ui.Dim : Ui.Sub;
            Cell(r, 0, Short(m.Key), Ui.Text, false);
            Cell(r, 1, Num(m.Max, m.Unit), dim, true);
            Cell(r, 2, Num(m.Avg, m.Unit), dim, true);
            Cell(r, 3, m.Sustained is { } s ? Num(s, m.Unit) : "–", dim, true);
            r++;
        }
        parent.Children.Add(grid);
    }

    private static string Num(double v, string unit)
    {
        string f = unit switch { "W" or "MB/s" => "0.0", _ => "0" };
        return v.ToString(f, CultureInfo.InvariantCulture) + " " + unit;
    }

    private string SpanLine(SpanSummary sp)
    {
        var parts = new List<string>();
        foreach (var m in sp.Metrics)
        {
            string name = Short(m.Key);
            // GPU の項目は、グループ内の短い名前から番号を省いてあるので足す（CPU の名前には CPU が入っている）
            string dev = MetricKeys.TryParseGpu(m.Key, out int gi, out _) ? "GPU" + gi + " " : "";
            string v = m.Key.EndsWith("temp_c", StringComparison.Ordinal) ? $"{L.T("最大", "max")} {Num(m.Max, m.Unit)}"
                     : m.Key.EndsWith("clock_mhz", StringComparison.Ordinal) ? $"{L.T("中央値", "median")} {Num(m.Median, m.Unit)}"
                     : $"{L.T("平均", "avg")} {Num(m.Avg, m.Unit)}";
            parts.Add($"{dev}{name} {v}");
        }
        string span = $"{T.SpanLabel(sp.Kind, sp.Open)} {T.Elapsed(TimeSpan.FromSeconds(sp.StartSec))}〜{T.Elapsed(TimeSpan.FromSeconds(sp.EndSec))}";
        return span + ": " + string.Join(" · ", parts);
    }

    private static TextBlock Muted(string text)
    {
        var t = Ui.Label(text, 11, Ui.Dim);
        t.Margin = new Thickness(0, 6, 0, 0);
        return t;
    }

    // =====================================================================
    // AI に渡す用
    // =====================================================================

    private async Task BuildAiAsync()
    {
        if (_view is not { } v) return;
        _ai.Children.Clear();
        var box = new StackPanel();
        box.Children.Add(Ui.Heading(T.AiHeading, first: true));
        var progress = Ui.Label(T.AiBuilding, 12, Ui.Sub);
        box.Children.Add(progress);
        _ai.Children.Add(Ui.Card(box));
        try
        {
            var export = await Task.Run(() => AiExport.Build(v.Session, v.Summary, CsvStream.Read(v.CsvPath), _lang));
            if (_closed) return;
            _aiText = export.Text;
            box.Children.Remove(progress);
            box.Children.Add(Ui.Label(T.AiIntro, 11, Ui.Dim));
            if (export.WithheldNames > 0) box.Children.Add(Ui.Label(T.AiWithheld(export.WithheldNames), 11, Ui.Yellow));
            var preview = new TextBox
            {
                Text = export.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 300, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(6, 4, 6, 4),
                FontFamily = new FontFamily("Consolas, Yu Gothic UI"), FontSize = 11, Foreground = Ui.Text, Background = Ui.Back,
                BorderBrush = Ui.Edge, CaretBrush = Ui.Text,
            };
            box.Children.Add(preview);
            var copy = Ui.Button(T.AiCopy, primary: true);
            var save = Ui.Button(T.AiSave);
            var msg = Ui.Label("", 12, Ui.Sub);
            msg.VerticalAlignment = VerticalAlignment.Center;
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(export.Text); msg.Text = T.AiCopied; }
                catch (System.Runtime.InteropServices.COMException) { msg.Text = T.BuildClipboardFailed; }
            };
            save.Click += (_, _) => SaveAi(export.Text, msg);
            box.Children.Add(Ui.Row(copy, save, msg));
        }
        catch (ExportBlockedException)
        {
            if (_closed) return;
            box.Children.Remove(progress);
            box.Children.Add(Ui.Label(T.AiBlocked, 12, Ui.Red));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            if (_closed) return;
            box.Children.Remove(progress);
            box.Children.Add(Ui.Label(T.Failed + ex.Message, 12, Ui.Red));
        }
    }

    private void SaveAi(string text, TextBlock msg)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = T.AiSaveTitle,
            // 実行の ID は入れない（ファイル名から ID が漏れないように）。日時はこの PC の時計
            FileName = "partslife-ai-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt",
            DefaultExt = ".txt",
            Filter = "Text (*.txt)|*.txt|Markdown (*.md)|*.md",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            msg.Text = T.AiSaved(Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { msg.Text = T.Failed + ex.Message; }
    }

    // =====================================================================
    // 書き出し（--shot-check）・閉じるとき
    // =====================================================================

    /// <summary>
    /// 書き出しのために、一時フォルダに 6 秒だけ本当に記録して要約と書き出しの見本まで出す。
    /// ドキュメントには書かない。終わったら一時フォルダごと消す。
    /// </summary>
    public override async Task PrepareShotAsync()
    {
        try
        {
            await StartAsync();
            if (_phase != Phase.Recording) return;
            await Task.Delay(TimeSpan.FromSeconds(3));
            AddMark(MarkerKind.BenchStart);
            await Task.Delay(TimeSpan.FromSeconds(3));
            AddMark(MarkerKind.BenchEnd);
            await EndRecordingAsync("user");
            await BuildAiAsync();
        }
        finally
        {
            try { Directory.Delete(_folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>窓を閉じる・本体が終わるとき: 記録中なら止めて、ここまでの分を保存する（待たない。窓の片付けが終わりを待つ）。</summary>
    public override void Stop()
    {
        _closed = true;
        _timer.Stop();
        if (_rec is { IsRecording: true } rec)
        {
            _task = Task.Run(() =>
            {
                try { rec.StopAsync("window-closed").GetAwaiter().GetResult(); } catch (RecordException) { }
                rec.Dispose();
            });
        }
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}
