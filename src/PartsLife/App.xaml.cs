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

        int story = Array.FindIndex(args, a => a == "--story");
        if (story >= 0 && story + 1 < args.Length)
        {
            DumpStory(args[story + 1], LangArg(args) ?? "ja");
            Shutdown();
            return;
        }

        int shotCheck = Array.FindIndex(args, a => a == "--shot-check");
        if (shotCheck >= 0)
        {
            // 引数が足りなくても常駐の窓には落ちない（書き出しのつもりで保存を動かさない）
            if (shotCheck + 2 < args.Length) ShotCheck(args, shotCheck);
            else Shutdown(2);
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

        // 常駐の窓が本体。閉じたらアプリごと終わる（点検の窓が開いていても残らない。App.xaml の ShutdownMode）
        var main = new MainWindow();
        MainWindow = main;
        int bench = Array.IndexOf(args, "--bench");
        if (bench >= 0 && bench + 1 < args.Length) main.BenchMode = args[bench + 1];
        main.Show();
    }

    /// <summary>
    /// `--story <出力先.txt>` — 物語の中身を点検する。
    /// **どの条件がどの機械で出るのかは、実機に置いて数週間待たないと分からない。**
    /// それでは書いた物が正しく繋がっているか確かめようがないので、
    /// 条件を仮定して全場面を書き出せるようにしてある。
    /// </summary>
    private static void DumpStory(string path, string lang)
    {
        var story = Game.GameAssets.LoadStory();
        var sb = new System.Text.StringBuilder();

        // 育ち具合を変えて2通り書き出す。分岐が効いていれば違う文が出る
        var worlds = new (string Name, Game.WorldState W)[]
        {
            ("はじめて / fresh", new Game.WorldState(
                new Dictionary<string, int>(), 0, 1, 1,
                new HashSet<string> { "cpu", "cpu-cooler", "memory", "gpu", "ssd", "psu" }, 1, 0)),
            ("育った / grown", new Game.WorldState(
                new Dictionary<string, int>
                {
                    ["cpu"] = 20, ["cpu-cooler"] = 20, ["memory"] = 18, ["gpu"] = 22,
                    ["m2"] = 16, ["ssd"] = 14, ["hdd"] = 12, ["psu"] = 19, ["motherboard"] = 10,
                }, 20, 3, 2,
                new HashSet<string> { "cpu", "cpu-cooler", "memory", "gpu", "m2", "ssd", "hdd", "psu" },
                300, 1200)),
        };

        foreach (var (name, w) in worlds)
        {
            sb.AppendLine($"================ {name} ================");
            foreach (var ch in story.Chapters)
            {
                sb.AppendLine($"\n--- {ch.Title.For(lang)}  (旅程 {ch.Require:N0} / 1場面 {ch.ScenePer:N0}) ---");
                foreach (var sc in ch.Scenes)
                {
                    var line = sc.Pick(w);
                    sb.AppendLine("  " + (line?.For(lang) ?? "(出せる文がありません)"));
                }
            }
            int usable = story.Vignettes.Count(v => Game.Conditions.Match(v.When, w));
            sb.AppendLine($"\n小話: {usable} / {story.Vignettes.Count} 本がこの機械で出せます");
            foreach (var ev in story.Events)
            {
                int n = ev.Texts.Count(t => Game.Conditions.Match(t.When, w));
                sb.AppendLine($"  傍白 {ev.When,-16} {n,3} / {ev.Texts.Count,3} 本");
            }
        }
        sb.AppendLine($"\n文章の総数 {story.LineCount}");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// `--shot-check <出力先.png> <system|load|storage> [拡大率] [--lang en] [--width 680]` —
    /// **点検の窓の一面を、窓を出さずに PNG にして終わる。** 配布ページの画像用（--shot の点検版）。
    /// system は管理者なしのシステムチェックを実際に走らせて結果を描く。load は待っている面、
    /// storage は管理者の要る読み取りの前の面（補助 exe は起こさない）。保存にも checks にも書かない。
    /// **この版で止めてある面（Features で false の面）は描かない。** 理由を標準エラーと `<出力先>.error.txt` に出し、終了コード 3
    /// （v0.7.0 の storage がそうだった。v0.7.1 で storage は描けるようになった。補助 exe は今も起こさない）。
    ///
    /// **別の関数に分け、インライン化させない。** 点検のアセンブリ（PartsLife.CheckWindow / PartsLife.Checks）は、
    /// この関数が呼ばれたときに初めて読み込まれる。OnStartup に直接書くと、常駐の起動でも読み込まれてしまう。
    /// 失敗したら終了コード 1 で、理由を `<出力先>.error.txt` に書く。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async void ShotCheck(string[] args, int at)
    {
        string path = args[at + 1], tab = args[at + 2];
        double scale = at + 3 < args.Length && double.TryParse(args[at + 3], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0;
        int wAt = Array.IndexOf(args, "--width");
        double width = wAt >= 0 && wAt + 1 < args.Length && double.TryParse(args[wAt + 1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var wd) ? wd : 680;
        int code = 0;
        try
        {
            await PartsLife.CheckWindow.CheckWindowShot.SaveAsync(path, tab, scale, LangArg(args) ?? "ja", width);
        }
        catch (PartsLife.CheckWindow.FeatureDisabledException ex)
        {
            code = 3;
            try { Console.Error.WriteLine(ex.Message); } catch (IOException) { }
            try { File.WriteAllText(path + ".error.txt", ex.Message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            code = 1;
            try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Shutdown(code);
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
        var adapters = Sensors.GpuIdentity_.Enumerate();
        var real = Hardware.GpuRoster.Real(adapters).Select(a => a.LuidKey).ToHashSet();
        foreach (var g in adapters)
        {
            // 盤に立てない物には理由を添える。**「何枚に見えているか」と「何枚立てるか」を並べて見られるように**
            string why = real.Contains(g.LuidKey) ? "" : g.Software ? "  ← 除外（ソフトウェア）" : "  ← 除外（別名・中身なし）";
            sb.AppendLine($"  luid={g.LuidKey,-14} {(g.Name.Length > 0 ? g.Name : "(名前なし)"),-28} {g.DedicatedBytes / 1073741824.0:0.00} GB" +
                          $"  pci={(g.PciBus is { } b ? b.ToString() : "-")}{why}");
        }

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
