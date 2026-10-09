using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PartsLife.Checks;
using PartsLife.Checks.Protocol;
using PartsLife.Checks.Scan;
using PartsLife.Checks.Storage;
using PartsLife.Checks.SystemInfo;

namespace PartsLife.CheckWindow;

/// <summary>
/// 記憶媒体の精密チェック。読むのは管理者の補助 exe、判定と基準値の記録はこちら（利用者の権限）。
/// </summary>
internal sealed class StoragePanel : CheckPanel
{
    private readonly Button _read, _copy, _scanStart, _scanStop;
    private readonly TextBlock _status, _scanLive;
    private readonly StackPanel _results = new();
    private readonly StackPanel _scanResults = new();
    private readonly StackPanel _diskChoices = new();
    private readonly Ui.Choice<bool> _mode;
    private readonly ProgressBar _scanProgress = Ui.Progress();
    private Ui.Choice<int>? _disk;
    private bool _disksListed;
    private readonly BaselineStore _store = new();
    private readonly List<Finding> _lastText = new();

    private CancellationTokenSource? _cts;
    private Task _task = Task.CompletedTask;
    public override Task Running => _task;

    public StoragePanel()
    {
        Children.Add(Ui.Label(T.StorageIntro, 12, Ui.Sub));
        _read = Ui.Button(T.ReadSmart, primary: true);
        _copy = Ui.Button(T.Copy);
        _copy.IsEnabled = false;
        _status = Ui.Label("", 12, Ui.Sub);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Ui.Row(_read, _copy, _status));
        Children.Add(_results);

        Children.Add(Ui.Heading(T.ScanHeading));
        Children.Add(Ui.Label(T.ScanIntro, 12, Ui.Sub));
        Children.Add(Ui.FieldRow(T.Disk, _diskChoices));
        _mode = new Ui.Choice<bool>(new[] { (T.Sampled, false), (T.Full, true) }, false);
        Children.Add(Ui.FieldRow(T.Mode, _mode));
        _scanStart = Ui.Button(T.ScanStart, primary: true);
        _scanStop = Ui.Button(T.Stop);
        _scanStop.IsEnabled = false;
        _scanStart.IsEnabled = false;
        var row = Ui.Row(_scanStart, _scanStop);
        row.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(row);
        Children.Add(_scanProgress);
        _scanLive = Ui.Label("", 12, Ui.Sub);
        Children.Add(_scanLive);
        Children.Add(_scanResults);
        // 書き出しでは利用者名の入った実際の場所を出さない（配布ページに載せるため）
        Children.Add(Ui.Label(T.StorageFootnote(Ui.ShotMode ? @"%APPDATA%\PartsLife\checks" : _store.Directory), 11, Ui.Dim));

        _read.Click += async (_, _) => await ReadSmartAsync();
        _copy.Click += (_, _) => { try { Clipboard.SetText(Ui.ToText(_lastText)); } catch (System.Runtime.InteropServices.COMException) { } };
        _scanStart.Click += async (_, _) => await ScanAsync();
        _scanStop.Click += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_disksListed) _ = ListDisksAsync(); };
    }

    /// <summary>書き出しでは、管理者なしで読める台の一覧までを出す（補助 exe は起こさない）。</summary>
    public override Task PrepareShotAsync() => _disksListed ? Task.CompletedTask : ListDisksAsync();

    /// <summary>表面検査の対象の一覧。管理者なしで読める WMI から（開いた面が見えたときに初めて引く）。</summary>
    private async Task ListDisksAsync()
    {
        _disksListed = true;
        _diskChoices.Children.Add(Ui.Label(T.Working, 12, Ui.Sub));
        List<PhysicalDiskInfo> disks;
        try { disks = await Task.Run(SystemCheck.ReadDisks); }
        catch (Exception) { disks = new(); }
        _diskChoices.Children.Clear();
        if (disks.Count == 0) { _diskChoices.Children.Add(Ui.Label(T.NoDisks, 12, Ui.Dim)); return; }
        _disk = new Ui.Choice<int>(disks.Select(d =>
        {
            string letters = d.Volumes.Count > 0 ? " " + string.Join(" ", d.Volumes.Select(v => v.Letter)) : "";
            return ($"#{d.Number} {Short(d.Model)}{letters}", (int)d.Number);
        }), (int)disks[0].Number);
        _diskChoices.Children.Add(_disk);
        _scanStart.IsEnabled = HelperSession.FindHelper() is not null;
    }

    private static string Short(string model) => model.Length > 28 ? model[..28] + "…" : model;

    // ---------------------------------------------------------------------
    // SMART
    // ---------------------------------------------------------------------
    private async Task ReadSmartAsync()
    {
        if (Ui.ShotMode) return;   // 書き出し（--shot-check）の間は補助 exe を起こさない
        if (HelperSession.FindHelper() is null) { _status.Text = T.HelperMissing; return; }
        _read.IsEnabled = _scanStart.IsEnabled = _copy.IsEnabled = false;
        _results.Children.Clear();
        _lastText.Clear();
        _status.Text = T.AskingUac;
        _cts = new CancellationTokenSource();
        List<DiskReport>? reports = null;
        string? helperError = null;
        try
        {
            var t = HelperSession.RunAsync($"--lang {L.Lang} smart", m =>
            {
                if (m.Type == HelperMessage.Disks) reports = m.DiskReports;
                else if (m.Type == HelperMessage.Error) helperError = m.Message;
            }, _cts.Token);
            _task = t;
            var (outcome, detail) = await t;
            if (outcome != HelperOutcome.Completed || reports is null)
            {
                _status.Text = T.Outcome(outcome, helperError ?? detail);
                return;
            }

            // 全台まとめて記録する（同じ識別子の台が同時にあるかを見分けるため）
            var ordered = reports.OrderBy(r => r.Number).ToList();
            var outcomes = _store.RecordAll(ordered, DateTimeOffset.Now);
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                var o = outcomes[i];
                var j = StorageJudge.Judge(r, o.Previous, o.State);
                _results.Children.Add(Ui.Card(DiskCard(j, o.Error)));
                _lastText.Add(new Finding($"#{r.Number} {r.Model}", j.Overall, $"{BusNames.Of(r.Bus)} {r.SizeBytes / 1e9:0} GB  S/N {SerialShown(r.Serial)}"));
                _lastText.AddRange(j.Findings.Select(f => f with { Title = "  " + f.Title }));
            }
            _status.Text = T.SmartDone(reports.Count);
            _copy.IsEnabled = true;
        }
        catch (Exception ex) { _status.Text = T.Failed + ex.Message; }
        finally
        {
            _read.IsEnabled = true;
            _scanStart.IsEnabled = _disk is not null;
        }
    }

    private static UIElement DiskCard(DiskJudgement j, string? recordError)
    {
        var r = j.Report;
        var box = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var badge = new Border
        {
            Background = Ui.VerdictBrush(j.Overall), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = L.VerdictLabel(j.Overall), Foreground = Ui.Back, FontWeight = FontWeights.SemiBold, FontSize = 11 },
        };
        head.Children.Add(badge);
        head.Children.Add(Ui.Label($"#{r.Number} {r.Model}", 13, Ui.Text, bold: true, wrap: false));
        box.Children.Add(head);
        string serial = r.Serial.Length > 0 ? $" · S/N {SerialShown(r.Serial)}" : "";
        string fw = r.Firmware.Length > 0 ? $" · FW {r.Firmware}" : "";
        box.Children.Add(Ui.Label($"{BusNames.Of(r.Bus)} · {r.SizeBytes / 1e9:0} GB{serial}{fw}", 11, Ui.Dim));

        foreach (var f in j.Findings) box.Children.Add(Ui.FindingRow(f));

        if (j.Previous is { } prev)
        {
            var grew = j.Deltas.Where(d => d.Change != 0).ToList();
            box.Children.Add(Ui.Label(T.SinceLast(prev.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), 11, Ui.Sub));
            foreach (var d in grew)
                box.Children.Add(Ui.Label("  " + d.Format(), 11, Counters.GrowthVerdict(d.Counter) is { } v && d.Grew ? Ui.VerdictBrush(v) : Ui.Sub));
            if (grew.Count == 0) box.Children.Add(Ui.Label("  " + T.NoChange, 11, Ui.Sub));
        }
        else if (BaselineStates.WhyNotCompared(j.Baseline) is { } why)
            box.Children.Add(Ui.Label(why, 11, Ui.Sub));
        else if (r.Unavailable is null && j.Baseline == BaselineState.First)
            box.Children.Add(Ui.Label(T.FirstBaseline, 11, Ui.Sub));
        if (recordError is not null)
            box.Children.Add(Ui.Label(T.RecordFailed + recordError, 11, Ui.Yellow));

        // 属性の一覧（ATA）。開いたときだけ作る
        if (r.Ata.Count > 0)
        {
            var toggle = Ui.Button(T.AttributeTable);
            toggle.Margin = new Thickness(0, 6, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            var table = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, MS Gothic"), FontSize = 11, Foreground = Ui.Sub,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0),
            };
            toggle.Click += (_, _) =>
            {
                if (table.Text.Length == 0) table.Text = AttributeTable(r.Ata);
                table.Visibility = table.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
            box.Children.Add(toggle);
            box.Children.Add(table);
        }
        return box;
    }

    /// <summary>画面にはそのまま、書き出し（--shot-check）では末尾 4 文字だけ。</summary>
    private static string SerialShown(string serial) => Ui.ShotMode ? CheckWindowShot.MaskSerial(serial) : serial;

    private static string AttributeTable(IReadOnlyList<SmartAttribute> attrs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"ID",3}  {"Cur",3} {"Wst",3} {"Thr",3}  {"Raw",15}  Name");
        foreach (var a in attrs)
            sb.AppendLine($"{a.Id,3}  {a.Current,3} {a.Worst,3} {(a.Threshold?.ToString() ?? "-"),3}  {a.Raw,15}  {a.Name}{(a.FailingNow ? "  <-- " + T.BelowThreshold : "")}");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------------
    // 表面検査
    // ---------------------------------------------------------------------
    private async Task ScanAsync()
    {
        if (_disk is null || Ui.ShotMode) return;   // 書き出しの間は補助 exe を起こさない
        if (HelperSession.FindHelper() is null) { _scanLive.Text = T.HelperMissing; return; }
        int disk = _disk.Value;
        bool full = _mode.Value;
        _scanResults.Children.Clear();
        _scanProgress.Value = 0;
        _scanLive.Text = T.AskingUac;
        _read.IsEnabled = _scanStart.IsEnabled = false;
        _disk.IsEnabled = _mode.IsEnabled = false;
        _scanStop.IsEnabled = true;
        _cts = new CancellationTokenSource();
        ScanResult? result = null;
        string? helperError = null;
        try
        {
            string job = $"--lang {L.Lang} scan --disk {disk} --mode {(full ? "full" : "sample")} --every 256";
            var t = HelperSession.RunAsync(job, m =>
            {
                // パイプの読み取りは別のスレッドで進むので、画面には Dispatcher 経由で
                Dispatcher.BeginInvoke(() =>
                {
                    if (m.Type == HelperMessage.Progress && m.ScanProgress is { } p)
                    {
                        _scanProgress.Value = p.PlannedBytes <= 0 ? 0 : (double)p.DoneBytes / p.PlannedBytes;
                        _scanLive.Text = T.ScanLive(p);
                    }
                });
                if (m.Type == HelperMessage.Scan) result = m.ScanResult;
                else if (m.Type == HelperMessage.Error) helperError = m.Message;
            }, _cts.Token);
            _task = t;
            var (outcome, detail) = await t;
            if (result is null)
            {
                _scanLive.Text = T.Outcome(outcome, helperError ?? detail);
                return;
            }
            _scanLive.Text = result.Stopped ? T.StoppedPartial : T.ScanDone;
            var box = new StackPanel();
            foreach (var f in ScanJudge.Judge(result)) box.Children.Add(Ui.FindingRow(f));
            _scanResults.Children.Add(Ui.Card(box));
        }
        catch (Exception ex) { _scanLive.Text = T.Failed + ex.Message; }
        finally
        {
            _read.IsEnabled = _scanStart.IsEnabled = true;
            _disk.IsEnabled = _mode.IsEnabled = true;
            _scanStop.IsEnabled = false;
        }
    }

    public override void Stop() => _cts?.Cancel();
}
