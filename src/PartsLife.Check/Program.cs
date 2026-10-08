using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PartsLife.Checks;
using PartsLife.Checks.Protocol;
using PartsLife.Checks.Scan;
using PartsLife.Checks.Storage;

namespace PartsLife.Check;

/// <summary>
/// 管理者の補助 exe。点検の窓（PartsLife.exe）から UAC を経て起動される。
///
///   PartsLife.Check.exe --pipe PartsLife.Check.&lt;32桁&gt; --parent &lt;pid&gt; [--lang ja|en] smart
///   PartsLife.Check.exe --pipe ... --parent ... scan --disk &lt;n&gt; --mode sample|full [--every &lt;k&gt;]
///
/// ■ やること
/// - smart: 全物理ディスクの健康情報（NVMe の Health/Error ログ、ATA の SMART）を読む
/// - scan:  1台を読むだけの表面検査
/// 結果を JSON Lines でパイプに流し、終わったら閉じて終わる。
/// ■ やらないこと
/// - 媒体・ファイル・レジストリに書かない。ネットワークに繋がない
/// - 引数で決まった仕事以外はしない。パイプから受け付けるのは "stop" だけ
/// - 繋ぎ先が起動元（--parent）のパイプでなければ、何も読まずに終わる
/// </summary>
internal static partial class Program
{
    private static readonly object WriteGate = new();
    private static StreamWriter? _out;

    [GeneratedRegex("^PartsLife\\.Check\\.[0-9a-f]{32}$")]
    private static partial Regex PipeName();

    private static int Main(string[] args)
    {
        var a = Args.Parse(args);
        if (a is null) return 2;
        L.Lang = a.Lang;

        using var cts = new CancellationTokenSource();
        NamedPipeClientStream pipe;
        try
        {
            // Asynchronous で開く。同期の handle だと、"stop" を待つ読み取りが
            // 結果の書き込みを塞いでしまう（同じ handle の同期 I/O は順番待ちになる）
            pipe = new NamedPipeClientStream(".", a.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
        }
        catch (Exception) { return 3; }

        using (pipe)
        {
            // 繋いだ相手が、起動元のプロセスが作ったパイプか
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != (uint)a.Parent)
                return 4;

            _out = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Send(new HelperMessage { Type = HelperMessage.Hello, Pid = Environment.ProcessId });

            // 受け付けるのは "stop" だけ。パイプが切れた（窓が閉じた）ら止める
            var reader = new Thread(() =>
            {
                try
                {
                    using var r = new StreamReader(pipe, new UTF8Encoding(false), false, 256, leaveOpen: true);
                    while (true)
                    {
                        string? line = r.ReadLine();
                        if (line is null) break;
                        if (line.Trim() == HelperMessage.StopCommand) break;
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                cts.Cancel();
            }) { IsBackground = true };
            reader.Start();

            // 起動元が終わったら止める
            var parentWatch = new Thread(() =>
            {
                try { using var p = Process.GetProcessById(a.Parent); p.WaitForExit(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                cts.Cancel();
            }) { IsBackground = true };
            parentWatch.Start();

            try
            {
                if (a.Job == "smart")
                {
                    var disks = StorageReader.ReadAll(cts.Token);
                    Send(new HelperMessage { Type = HelperMessage.Disks, DiskReports = disks });
                }
                else
                {
                    var result = SurfaceScanner.Run(a.Disk, a.Full, a.Every, cts.Token,
                        p => Send(new HelperMessage { Type = HelperMessage.Progress, ScanProgress = p }));
                    Send(new HelperMessage { Type = HelperMessage.Scan, ScanResult = result });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Send(new HelperMessage { Type = HelperMessage.Error, Message = ex.GetType().Name + ": " + ex.Message });
            }
            Send(new HelperMessage { Type = HelperMessage.Done });
        }
        return 0;
    }

    private static void Send(HelperMessage m)
    {
        lock (WriteGate)
        {
            try { _out?.WriteLine(JsonSerializer.Serialize(m, HelperJson.Default.HelperMessage)); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed record Args(string Pipe, int Parent, string Lang, string Job, int Disk, bool Full, int Every)
    {
        /// <summary>**知らない引数があれば何もせずに終わる。** 余計な解釈をしない。</summary>
        public static Args? Parse(string[] args)
        {
            string? pipe = null, job = null, lang = "ja", mode = null;
            int parent = -1, disk = -1, every = 256;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (args[i])
                {
                    case "--pipe": pipe = Next(); break;
                    case "--parent": if (!int.TryParse(Next(), out parent)) return null; break;
                    case "--lang": lang = Next() == "en" ? "en" : "ja"; break;
                    case "--disk": if (!int.TryParse(Next(), out disk)) return null; break;
                    case "--mode": mode = Next(); break;
                    case "--every": if (!int.TryParse(Next(), out every)) return null; break;
                    case "smart" or "scan" when job is null: job = args[i]; break;
                    default: return null;
                }
            }
            if (pipe is null || !PipeName().IsMatch(pipe) || parent <= 0 || job is null) return null;
            if (job == "scan")
            {
                if (disk is < 0 or >= StorageReader.MaxDisks) return null;
                if (mode is not ("sample" or "full")) return null;
                if (every is < 2 or > 65536) return null;
            }
            return new Args(pipe, parent, lang, job, disk, mode == "full", every);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
