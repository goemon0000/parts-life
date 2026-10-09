using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PartsLife.Checks;
using PartsLife.Checks.Gpu;
using PartsLife.Checks.Load;

namespace PartsLife.CheckWindow;

/// <summary>
/// 負荷テスト。CPU / メモリ / GPU のどれか1つを、決めた時間だけ。
/// **GPU は <see cref="Features.GpuLoadTest"/> が true の版だけ選べる**（v0.7.0・v0.7.1 は選択肢そのものを出さない。実機での確認がまだのため）。
/// </summary>
internal sealed class LoadPanel : CheckPanel
{
    internal enum Kind { Cpu, Memory, Gpu }

    /// <summary>この版で選べる種類。窓に並ぶ順。</summary>
    internal static IReadOnlyList<Kind> Kinds { get; } =
        new[] { (Kind.Cpu, true), (Kind.Memory, true), (Kind.Gpu, Features.GpuLoadTest) }
            .Where(k => k.Item2).Select(k => k.Item1).ToArray();

    private readonly Ui.Choice<Kind> _kind;
    private readonly Ui.Choice<int> _duration;
    private readonly Ui.Choice<double> _fraction;
    private readonly Grid _fractionRow;
    private readonly StackPanel _gpuRow;
    private readonly StackPanel _gpuChoices = new();
    private readonly TextBlock _gpuNote;
    private Ui.Choice<int>? _gpu;
    private IReadOnlyList<GpuAdapter> _adapters = Array.Empty<GpuAdapter>();
    private bool _gpuListed;

    private readonly Button _start, _stop, _copy;
    private readonly ProgressBar _progress = Ui.Progress();
    private readonly TextBlock _live;
    private readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, Height = 38, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _results = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _elapsed = new();
    private TimeSpan _planned;
    private IReadOnlyList<Finding> _last = Array.Empty<Finding>();

    private CancellationTokenSource? _cts;
    private Task _task = Task.CompletedTask;
    public override Task Running => _task;

    public LoadPanel()
    {
        Children.Add(Ui.Label(T.LoadIntro, 12, Ui.Sub));

        _kind = new Ui.Choice<Kind>(Kinds.Select(k => (KindTitle(k), k)), Kind.Cpu);
        Children.Add(Ui.FieldRow(T.Kind, _kind));

        _duration = new Ui.Choice<int>(new[] { (T.Seconds(30), 30), (T.Minutes(2), 120), (T.Minutes(5), 300) }, 30);
        Children.Add(Ui.FieldRow(T.Duration, _duration));

        _fraction = new Ui.Choice<double>(new[] { (T.OfFree(10), 0.10), (T.OfFree(25), 0.25), (T.OfFree(50), 0.50) }, 0.25);
        _fractionRow = Ui.FieldRow(T.Amount, _fraction);
        _fractionRow.Visibility = Visibility.Collapsed;
        Children.Add(_fractionRow);

        _gpuNote = Ui.Label(T.GpuNote, 11, Ui.Dim);
        _gpuRow = new StackPanel { Visibility = Visibility.Collapsed };
        _gpuRow.Children.Add(Ui.FieldRow("GPU", _gpuChoices));
        _gpuRow.Children.Add(_gpuNote);
        Children.Add(_gpuRow);

        _start = Ui.Button(T.Start, primary: true);
        _stop = Ui.Button(T.Stop);
        _stop.IsEnabled = false;
        _copy = Ui.Button(T.Copy);
        _copy.IsEnabled = false;
        var buttons = Ui.Row(_start, _stop, _copy);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        Children.Add(buttons);

        Children.Add(_progress);
        _live = Ui.Label("", 12, Ui.Sub);
        Children.Add(_live);
        Children.Add(_bars);
        Children.Add(_results);
        Children.Add(Ui.Label(T.LoadFootnote, 11, Ui.Dim));

        _kind.Changed += k =>
        {
            _fractionRow.Visibility = k == Kind.Memory ? Visibility.Visible : Visibility.Collapsed;
            _gpuRow.Visibility = k == Kind.Gpu ? Visibility.Visible : Visibility.Collapsed;
            if (k == Kind.Gpu && !_gpuListed) ListGpus();
            UpdateStartEnabled();
        };
        _start.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => Stop();
        _copy.Click += (_, _) => { try { Clipboard.SetText(Ui.ToText(_last)); } catch (System.Runtime.InteropServices.COMException) { } };
        _clock.Tick += (_, _) =>
        {
            if (_planned > TimeSpan.Zero) _progress.Value = Math.Min(1, _elapsed.Elapsed / _planned);
        };
    }

    private static string KindTitle(Kind k) => k switch
    {
        Kind.Cpu => "CPU",
        Kind.Memory => T.Memory,
        _ => "GPU",
    };

    /// <summary>並んでいる種類の題（試験で、GPU を出していないことを確かめるため）。</summary>
    internal IReadOnlyList<string> KindTitles => _kind.Titles;

    /// <summary>GPU の一覧は、GPU を選んだときに初めて引く（NVML も D3D もそれまで読み込まない）。</summary>
    private async void ListGpus()
    {
        _gpuListed = true;
        _gpuChoices.Children.Clear();
        _gpuChoices.Children.Add(Ui.Label(T.Working, 12, Ui.Sub));
        _adapters = await Task.Run(() =>
        {
            using var nvml = Nvml.TryOpen();
            return GpuAdapters.Enumerate(nvml);
        });
        _gpuChoices.Children.Clear();
        if (_adapters.Count == 0)
        {
            _gpuChoices.Children.Add(Ui.Label(T.NoGpu, 12, Ui.Dim));
            UpdateStartEnabled();
            return;
        }
        var first = _adapters.FirstOrDefault(a => a.CanWatchTemperature) ?? _adapters[0];
        _gpu = new Ui.Choice<int>(_adapters.Select(a => (a.Name, a.DxgiIndex)), first.DxgiIndex);
        foreach (var a in _adapters.Where(a => !a.CanWatchTemperature))
            _gpu.SetEnabled(a.DxgiIndex, false, T.CannotWatch(a.IsNvidia));
        _gpu.Changed += _ => UpdateStartEnabled();
        _gpuChoices.Children.Add(_gpu);
        UpdateStartEnabled();
    }

    private GpuAdapter? SelectedGpu => _gpu is null ? null : _adapters.FirstOrDefault(a => a.DxgiIndex == _gpu.Value);

    private void UpdateStartEnabled()
    {
        bool running = !_task.IsCompleted;
        bool gpuOk = _kind.Value != Kind.Gpu || SelectedGpu is { CanWatchTemperature: true };
        _start.IsEnabled = !running && gpuOk;
    }

    private async Task RunAsync()
    {
        var kind = _kind.Value;
        var duration = TimeSpan.FromSeconds(_duration.Value);
        _results.Children.Clear();
        _bars.Children.Clear();
        _copy.IsEnabled = false;
        _live.Text = T.Starting;
        _progress.Value = 0;
        _planned = duration;
        _elapsed.Restart();
        _clock.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _stop.IsEnabled = true;
        _start.IsEnabled = false;
        _kind.IsEnabled = _duration.IsEnabled = _fraction.IsEnabled = _gpuChoices.IsEnabled = false;

        try
        {
            switch (kind)
            {
                case Kind.Cpu:
                {
                    var t = CpuLoadTest.RunAsync(new CpuLoadOptions { Duration = duration }, new Progress<CpuTick>(ShowCpu), ct);
                    _task = t;
                    _last = CpuLoadTest.Judge(await t);
                    break;
                }
                case Kind.Memory:
                {
                    var t = MemoryTest.RunAsync(new MemoryTestOptions { Duration = duration, FractionOfAvailable = _fraction.Value },
                                                new Progress<MemoryTick>(ShowMemory), ct);
                    _task = t;
                    _last = MemoryTest.Judge(await t);
                    break;
                }
                case Kind.Gpu when Kinds.Contains(Kind.Gpu):
                {
                    var adapter = SelectedGpu!;
                    var nvml = Nvml.TryOpen();
                    try
                    {
                        int? idx = adapter.NvmlIndex;
                        Func<int?>? temp = nvml is not null && idx is { } i ? () => nvml.Temperature(i) : null;
                        var t = GpuLoadTest.RunAsync(new GpuLoadOptions
                        {
                            Duration = duration, AdapterIndex = adapter.DxgiIndex, AdapterName = adapter.Name, Temperature = temp,
                        }, new Progress<GpuTick>(ShowGpu), ct);
                        _task = t;
                        _last = GpuLoadTest.Judge(await t);
                    }
                    // 温度の読み取りが固まって nvml.dll の中に糸が残っていれば、Dispose は
                    // 終了も解放もせずに手放す（Nvml.Dispose。解放した場所で糸が動き出さないように）
                    finally { nvml?.Dispose(); }
                    break;
                }
                default:
                    // この版で選べない種類（GPU を止めてある版の Kind.Gpu）。選択肢に出していないので来ない
                    throw new InvalidOperationException(kind + " is not available in this build");
            }
            _live.Text = ct.IsCancellationRequested ? T.StoppedPartial : T.Finished(_elapsed.Elapsed);
            var box = new StackPanel();
            foreach (var f in _last) box.Children.Add(Ui.FindingRow(f));
            _results.Children.Add(Ui.Card(box));
            _copy.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _live.Text = T.Failed + ex.Message;
        }
        finally
        {
            _clock.Stop();
            _elapsed.Stop();
            _progress.Value = ct.IsCancellationRequested ? _progress.Value : 1;
            _stop.IsEnabled = false;
            _kind.IsEnabled = _duration.IsEnabled = _fraction.IsEnabled = _gpuChoices.IsEnabled = true;
            UpdateStartEnabled();
        }
    }

    private void ShowCpu(CpuTick t)
    {
        string clock = t.Clock.EffectiveMHz is { } mhz ? $" · {T.Clock} {mhz / 1000:0.00} GHz" : $" · {T.Clock} {T.NotExposed}";
        string load = t.Clock.LoadPercent is { } l ? $" · {T.Load} {l:0}%" : "";
        string errs = t.ComputeErrors > 0 ? $" · {T.ComputeErrors(t.ComputeErrors)}" : "";
        _live.Text = $"{t.Second}s · {T.Total} {t.Total:0} M/s{clock}{load}{errs}";

        // コア別の棒。一番速いコアを満杯にした相対の高さ
        double max = Math.Max(1e-9, t.PerCpu.Max());
        while (_bars.Children.Count < t.PerCpu.Length)
            _bars.Children.Add(new Border { Width = 6, Margin = new Thickness(0, 0, 2, 0), Background = Ui.Accent, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(1) });
        for (int i = 0; i < t.PerCpu.Length; i++)
        {
            var b = (Border)_bars.Children[i];
            b.Height = Math.Max(2, 36 * t.PerCpu[i] / max);
            b.ToolTip = $"#{i}  {t.PerCpu[i]:0.0} M/s";
        }
    }

    private void ShowMemory(MemoryTick t)
    {
        string phase = t.Verifying ? T.Verifying : T.Writing;
        string errs = t.Mismatches > 0 ? $" · {T.Mismatches(t.Mismatches)}" : "";
        _live.Text = $"{t.Bytes / 1073741824.0:0.0} GB · {T.Pass(t.Pass)} {t.Pattern} {phase} · {t.MBps / 1024:0.0} GB/s{errs}";
    }

    private void ShowGpu(GpuTick t)
    {
        string errs = t.Mismatches > 0 ? $" · {T.Mismatches(t.Mismatches)}" : "";
        _live.Text = $"{t.Second}s · {T.Temperature} {t.TemperatureC} °C · {t.GigaOpsPerSec:0.0} G/s{errs}";
    }

    public override void Stop() => _cts?.Cancel();
}
