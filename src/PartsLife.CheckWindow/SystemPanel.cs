using System.Windows;
using System.Windows.Controls;
using PartsLife.Checks;
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

/// <summary>システムチェック。管理者なし、数秒で終わる一覧。</summary>
internal sealed class SystemPanel : CheckPanel
{
    private readonly Button _run;
    private readonly Button _copy;
    private readonly TextBlock _status;
    private readonly StackPanel _results = new();
    private CancellationTokenSource? _cts;
    private Task _task = Task.CompletedTask;
    private List<SystemItem> _last = new();

    public override Task Running => _task;

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

    /// <summary>書き出しでは、管理者なしのシステムチェックを本当に走らせて、その結果を描く。</summary>
    public override Task PrepareShotAsync() => RunAsync();

    public override void Stop() => _cts?.Cancel();
}
