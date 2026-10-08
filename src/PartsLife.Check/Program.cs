using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PartsLife.Checks;
using PartsLife.Checks.Protocol;
using PartsLife.Checks.Scan;
using PartsLife.Checks.Storage;

// 名前で読み込む DLL は System32 からだけ（exe の隣に置かれた同名の DLL を拾わない）
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

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
///   （引数の読み方・パイプの確かめ方は PartsLife.Checks の HelperArgs / HelperPipe。試験もそちら）
/// - 繋ぎ先が起動元（--parent）のパイプでなければ、何も読まずに終わる
/// </summary>
internal static class Program
{
    private static readonly object WriteGate = new();
    private static StreamWriter? _out;

    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    private static int Main(string[] args)
    {
        // **何よりも先に。** 以後この process が読み込む DLL は System32 からだけ探す。
        // 効かなければ（そんな Windows は無いはずだが）管理者のまま先へ進まない
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)) return 5;
        PinNativeSearch();

        var a = HelperArgs.Parse(args);
        if (a is null) return 2;
        L.Lang = a.Lang;

        using var cts = new CancellationTokenSource();
        // 繋いだ先が、起動元のプロセスが作ったパイプでなければ何もしない
        switch (HelperPipe.ConnectToParent(a.Pipe, a.Parent, 10_000, out var connected))
        {
            case HelperPipe.Connect.NoServer: return 3;
            case HelperPipe.Connect.WrongServer: return 4;
        }
        var pipe = connected!;

        using (pipe)
        {
            _out = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Send(new HelperMessage { Type = HelperMessage.Hello, Pid = Environment.ProcessId });

            // 受け付けるのは "stop" だけ。パイプが切れた（窓が閉じた）ら止める
            var reader = new Thread(() =>
            {
                try
                {
                    using var r = new StreamReader(pipe, new UTF8Encoding(false), false, 256, leaveOpen: true);
                    HelperPipe.WaitForStop(r);
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

    /// <summary>
    /// .NET の標準ライブラリが実行時に読み込む DLL を、System32 の絶対パスからだけ読ませる。
    /// 標準ライブラリは「exe の隣 → System32」の順で探す指定になっていて、NativeAOT では
    /// exe の隣の同名ファイルを絶対パスで読み込む（SetDefaultDllDirectories では止まらない）。
    /// 対象は発行時の検査（csproj の CheckHelperNativeSearch）が挙げるアセンブリ。増えたらここにも足す。
    /// </summary>
    private static void PinNativeSearch()
    {
        foreach (var asm in new[]
        {
            typeof(System.Security.Principal.WindowsIdentity).Assembly,      // sspicli.dll, advapi32.dll
            typeof(Process).Assembly,                                         // ntdll.dll, kernel32.dll
            typeof(System.Security.AccessControl.ObjectSecurity).Assembly,   // advapi32.dll
            typeof(Microsoft.Win32.Registry).Assembly,                        // advapi32.dll
        })
            NativeLibrary.SetDllImportResolver(asm, FromSystem32);
    }

    private static IntPtr FromSystem32(string name, System.Reflection.Assembly asm, DllImportSearchPath? paths)
    {
        string file = Path.GetFileName(name);
        if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) file += ".dll";
        // 見つからなければ例外（既定の探し方には戻さない）
        return NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, file));
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);
}
