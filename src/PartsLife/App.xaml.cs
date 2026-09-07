using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PartsLife.Rendering;

namespace PartsLife;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 通常は窓を出すだけ。
    ///
    /// `--render <出力先.png> [負荷 0..1] [拡大率]` を付けると、**窓を出さずに基板を1枚
    /// PNG に書き出して終わる。** 見た目を直したとき、実機に置いて撮り直さずに
    /// 絵だけ確認できるようにするため（画面のある所でしか確認できないと、往復が増える）。
    /// </summary>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        int at = Array.FindIndex(args, a => a == "--render");
        if (at >= 0 && at + 1 < args.Length)
        {
            RenderToFile(args[at + 1],
                at + 2 < args.Length && double.TryParse(args[at + 2], out var l) ? l : 1.0,
                at + 3 < args.Length && int.TryParse(args[at + 3], out var s) ? s : 3);
            Shutdown();
            return;
        }

        int probe = Array.FindIndex(args, a => a == "--probe");
        if (probe >= 0 && probe + 1 < args.Length)
        {
            Probe(args[probe + 1]);
            Shutdown();
            return;
        }

        int shot = Array.FindIndex(args, a => a == "--shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            var w = new MainWindow();
            var bmp = w.RenderShot(
                expanded: !args.Contains("--collapsed"),
                scale: shot + 2 < args.Length && double.TryParse(args[shot + 2], out var sc) ? sc : 2,
                lang: LangArg(args));
            Save(bmp, args[shot + 1]);
            Shutdown();
            return;
        }

        var main = new MainWindow();
        int bench = Array.IndexOf(args, "--bench");
        if (bench >= 0 && bench + 1 < args.Length) main.BenchMode = args[bench + 1];
        main.Show();
    }

    /// <summary>`--lang en` で書き出す言語を指定する。宣材を日英ぶん作るため。</summary>
    private static string? LangArg(string[] args)
    {
        int at = Array.IndexOf(args, "--lang");
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// `--probe <出力先.txt>` — 実機が何をどう見えているかを書き出す。
    /// 窓を開かないと確認できないと、画面のある所へ行くまで直しの正否が分からない。
    /// </summary>
    private static void Probe(string path)
    {
        var sb = new System.Text.StringBuilder();
        // SSD/HDD の判別は別スレッドで走る。**先に作っておかないと、待っても間に合わない**
        var disks = new Sensors.DiskSampler();
        sb.AppendLine("== D3DKMT が返したアダプタ ==");
        foreach (var g in Sensors.GpuIdentity_.Enumerate())
            sb.AppendLine($"  luid={g.LuidKey,-14} {g.Name,-28} {g.DedicatedBytes / 1073741824.0:0.00} GB");

        using (var pdh = new Sensors.PdhSampler())
        {
            System.Threading.Thread.Sleep(1200);   // PDH は2回目の収集で初めて値が出る
            var (gpus, disk) = pdh.Sample();
            sb.AppendLine("== 画面に出る GPU の行 ==");
            for (int i = 0; i < gpus.Count; i++)
                sb.AppendLine($"  GPU{i}: {gpus[i].Name,-28} {gpus[i].Utilization * 100:0}%  " +
                              $"VRAM {gpus[i].VramUsedBytes / 1073741824.0:0.0} / {gpus[i].VramTotalBytes / 1073741824.0:0.0} GB");
            sb.AppendLine($"== ディスク == 読み {disk.ReadBytesPerSec / 1048576.0:0.0} / 書き {disk.WriteBytesPerSec / 1048576.0:0.0} MB/s");
        }

        System.Threading.Thread.Sleep(2500);
        sb.AppendLine("== 物理ディスク ==");
        if (Sensors.DiskSampler.LastProbeError is { } err) sb.AppendLine("  判別に失敗: " + err);
        foreach (var d in disks.Disks.OrderBy(d => d.Number))
            sb.AppendLine($"  #{d.Number}  {(d.IsNvme ? "M.2 (NVMe)" : d.IsSsd ? "SSD (SATA)" : "HDD")}");

        sb.AppendLine("== ドライブ ==");
        foreach (var d in disks.Sample())
            sb.AppendLine($"  {d.Name} {d.Label,-12} {d.UsedBytes / 1073741824.0:0} / {d.TotalBytes / 1073741824.0:0} GB" +
                          $"  {(d.IsNvme ? "M.2" : d.IsSsd ? "SSD" : "HDD")}  (disk #{d.DiskNumber})");

        File.WriteAllText(path, sb.ToString());
    }

    private static void RenderToFile(string path, double load, int scale)
    {
        var atlas = Atlas.Load();
        var board = new BoardRenderer(atlas);

        // 実機を模した並び。**GPU 2枚（開発機と同じ）と、サーバ想定の折り返しの両方を見る。**
        var desktop = new (string Id, SlotKind Kind)[]
        {
            ("cpu", SlotKind.Socket), ("cpu-cooler", SlotKind.Socket), ("memory", SlotKind.Dimm),
            ("gpu", SlotKind.Pcie), ("gpu", SlotKind.Pcie),
            ("ssd", SlotKind.M2), ("hdd", SlotKind.Sata), ("psu", SlotKind.Atx),
        };
        var server = new (string Id, SlotKind Kind)[]
        {
            ("cpu", SlotKind.Socket), ("cpu-cooler", SlotKind.Socket),
            ("cpu", SlotKind.Socket), ("cpu-cooler", SlotKind.Socket),
            ("memory", SlotKind.Dimm),
            ("gpu", SlotKind.Pcie), ("gpu", SlotKind.Pcie), ("gpu", SlotKind.Pcie), ("gpu", SlotKind.Pcie),
            ("ssd", SlotKind.M2), ("hdd", SlotKind.Sata), ("psu", SlotKind.Atx),
        };
        var layout = path.Contains("server", StringComparison.OrdinalIgnoreCase) ? server : desktop;

        // 何コマか縦に並べる。**1コマだけ見ても、湯気が動いているかは判断できない。**
        var frames = new List<BitmapSource>();
        for (int f = 0; f < 6; f++)
        {
            var slots = layout.Select(l => new BoardSlot(
                l.Id, l.Kind, load >= 0.85 ? "peek" : load >= 0.45 ? "walk" : "idle",
                f, 3, 0.5, load)).ToList();
            frames.Add(board.Render(slots));
        }

        int w = frames[0].PixelWidth, h = frames[0].PixelHeight;
        var dv = new System.Windows.Media.DrawingVisual();
        // 拡大は最近傍で。既定の補間だとドット絵がぼやけて、確認の役に立たない
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            dv, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, w, h * frames.Count));
            for (int i = 0; i < frames.Count; i++)
                dc.DrawImage(frames[i], new Rect(0, i * h, w, h));
        }
        var rtb = new RenderTargetBitmap(w * scale, h * frames.Count * scale, 96 * scale, 96 * scale,
                                         System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
