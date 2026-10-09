using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PartsLife.Checks.Protocol;

public enum HelperOutcome
{
    Completed,
    /// <summary>UAC で「いいえ」が押された。</summary>
    Declined,
    /// <summary>補助 exe が見つからない（単体 exe 版には同梱していない）。</summary>
    Missing,
    /// <summary>利用者が止めた / 窓が閉じられた。</summary>
    Stopped,
    Failed,
    /// <summary>この版では記憶媒体の精密チェックを止めてある（<see cref="Features.StorageCheck"/>）。何も起こしていない。</summary>
    Disabled,
}

/// <summary>
/// 管理者の補助 exe（PartsLife.Check.exe）を1回だけ起動し、結果を受け取る。**窓の側で使う。**
///
/// ■ 経路は名前付きパイプ1本
/// - 名前は毎回ランダム（推測して先回りされない）
/// - 繋ぎに来た相手が、いま起動した補助 exe 本人かをプロセス番号で確かめる
///   （補助 exe の側も、パイプの持ち主が起動元かを確かめる）
/// - パイプの権限は「本人」と「Administrators」だけ
/// ■ 窓を閉じたら止まる
/// パイプが切れたら補助 exe は仕事を止めて終わる。こちらから止めるときは <c>stop</c> を送る。
/// 昇格したプロセスは利用者の権限では強制終了できないので、**止める手段はこの2つに頼る。**
/// </summary>
public static class HelperSession
{
    public const string ExeName = "PartsLife.Check.exe";

    public const string OverrideVariable = "PARTSLIFE_CHECK_HELPER";

    /// <summary>
    /// 環境変数で補助 exe の場所を差し替えられるか。**Debug ビルドだけ。**
    /// 配布物（Release）で効くと、同じ利用者の権限で動く別のプログラムが HKCU\Environment に
    /// 書くだけで、次の点検で自分の exe を管理者で起動させられる（守りが UAC の確認画面だけになる）。
    /// </summary>
#if DEBUG
    public const bool OverrideAllowed = true;
#else
    public const bool OverrideAllowed = false;
#endif

    /// <summary>
    /// 補助 exe の置き場所。**本体と同じフォルダの絶対パスだけ。**
    /// 開発中（Debug ビルド）に限り、環境変数 PARTSLIFE_CHECK_HELPER で別の場所（絶対パス）を指せる。
    /// </summary>
    public static string? FindHelper() =>
        FindHelper(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(OverrideVariable));

    internal static string? FindHelper(string appDirectory, string? overridePath)
    {
#pragma warning disable CS0162 // Release では到達しない（それが狙い）
        if (OverrideAllowed && !string.IsNullOrEmpty(overridePath) && Path.IsPathFullyQualified(overridePath)
            && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);
#pragma warning restore CS0162
        if (!Path.IsPathFullyQualified(appDirectory)) return null;
        string here = Path.GetFullPath(Path.Combine(appDirectory, ExeName));
        return File.Exists(here) ? here : null;
    }

    /// <param name="job">補助 exe に渡す仕事（"smart" / "scan --disk 1 --mode sample --every 256"）。</param>
    /// <remarks>
    /// **補助 exe を管理者で起こすのは、ここだけ。** <see cref="Features.StorageCheck"/> が false の版では
    /// 探しもせずに <see cref="HelperOutcome.Disabled"/> を返す（窓の側でも面を出していないが、二重に止める）。
    /// </remarks>
    public static Task<(HelperOutcome Outcome, string? Detail)> RunAsync(
        string job, Action<HelperMessage> onMessage, CancellationToken ct)
    {
        Interlocked.Increment(ref _publicRuns);
        return RunAsync(Features.StorageCheck, FindHelper, ElevatedStart, job, onMessage, ct);
    }

    private static int _publicRuns;

    /// <summary>
    /// 上の入口（補助 exe を管理者で起こす唯一の道）が呼ばれた回数。試験で、
    /// **--shot-check storage がこの道に一度も入らないこと**を確かめるため。
    /// </summary>
    internal static int PublicRuns => Volatile.Read(ref _publicRuns);

    /// <summary>UAC を経て補助 exe を起こす（ShellExecute の runas）。</summary>
    private static Func<string, Process?> ElevatedStart(string exe) => args => Process.Start(new ProcessStartInfo(exe, args)
    {
        UseShellExecute = true,
        Verb = "runas",          // ここで UAC が出る
    });

    /// <summary>
    /// 上の本体。切り替え・探し方・起こし方を受け取るのは、**止めてある版で本当に何も起こさないこと**を
    /// 昇格せずに試験で確かめるため（切り替えは定数なので、引数にしないと「起こす側」を試せない）。
    /// </summary>
    internal static Task<(HelperOutcome Outcome, string? Detail)> RunAsync(
        bool enabled, Func<string?> find, Func<string, Func<string, Process?>> launcher,
        string job, Action<HelperMessage> onMessage, CancellationToken ct)
    {
        if (!enabled) return Task.FromResult<(HelperOutcome, string?)>((HelperOutcome.Disabled, null));
        string? exe = find();
        if (exe is null) return Task.FromResult<(HelperOutcome, string?)>((HelperOutcome.Missing, null));
        return RunCoreAsync(launcher(exe), job, onMessage, ct);
    }

    /// <summary>
    /// 本体。<paramref name="start"/> は引数の文字列を受けてプロセスを起こす。
    /// 普段は上の RunAsync（UAC 経由。切り替えを確かめた後）だけが使う。昇格せずにパイプの手順だけを確かめるときに差し替える。
    /// </summary>
    internal static async Task<(HelperOutcome Outcome, string? Detail)> RunCoreAsync(
        Func<string, Process?> start, string job, Action<HelperMessage> onMessage, CancellationToken ct)
    {
        string name = "PartsLife.Check." + Guid.NewGuid().ToString("N");
        using var pipe = CreatePipe(name);

        Process? proc;
        try
        {
            // UAC の確認が出ている間 Process.Start は戻らない。画面の糸で呼ぶと、その間
            // 常駐の窓まで止まるので、別の糸で待つ（.NET は STA の糸を立てて ShellExecuteEx を呼ぶ）
            string args = $"--pipe {name} --parent {Environment.ProcessId} {job}";
            proc = await Task.Run(() => start(args), CancellationToken.None);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED
        {
            return (HelperOutcome.Declined, null);
        }
        catch (Win32Exception ex)
        {
            return (HelperOutcome.Failed, ex.Message);
        }
        if (proc is null) return (HelperOutcome.Failed, "process did not start");

        using (proc)
        {
            // 繋ぎに来るのを待つ。補助 exe が先に終わってしまったら待たない
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));
            var exited = proc.WaitForExitAsync(connectCts.Token);
            var connect = pipe.WaitForConnectionAsync(connectCts.Token);
            try
            {
                if (await Task.WhenAny(connect, exited) != connect || !pipe.IsConnected)
                    return ct.IsCancellationRequested ? (HelperOutcome.Stopped, null)
                                                      : (HelperOutcome.Failed, "helper did not connect");
                await connect;
            }
            catch (OperationCanceledException)
            {
                return ct.IsCancellationRequested ? (HelperOutcome.Stopped, null) : (HelperOutcome.Failed, "helper did not connect");
            }

            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != (uint)proc.Id)
                return (HelperOutcome.Failed, "unexpected pipe client");

            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            using var stopReg = ct.Register(() =>
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(HelperMessage.StopCommand + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });

            bool done = false;
            while (true)
            {
                string? line;
                try
                {
                    // 止めた後も、補助 exe が締めの1行を送り終えるまでは読む（最大 10 秒）
                    var read = reader.ReadLineAsync(CancellationToken.None).AsTask();
                    if (ct.IsCancellationRequested)
                    {
                        if (await Task.WhenAny(read, Task.Delay(10_000, CancellationToken.None)) != read) break;
                    }
                    line = await read;
                }
                catch (IOException) { break; }
                if (line is null) break;
                if (line.Length == 0) continue;

                HelperMessage? msg;
                try { msg = JsonSerializer.Deserialize(line, HelperJson.Default.HelperMessage); }
                catch (JsonException) { continue; }
                if (msg is null) continue;
                if (msg.Type == HelperMessage.Done) { done = true; break; }
                onMessage(msg);
            }

            if (ct.IsCancellationRequested) return (HelperOutcome.Stopped, null);
            return done ? (HelperOutcome.Completed, null) : (HelperOutcome.Failed, "helper ended without finishing");
        }
    }

    /// <summary>
    /// 本人と Administrators だけが繋げるパイプ。**同じ名前のパイプが既にあれば作らない**
    /// （FirstPipeInstance。先回りして作られたパイプの後ろに並ばない）。
    /// </summary>
    internal static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        // 標準ユーザーが別の管理者の資格情報で昇格した場合、補助 exe は別人として動く
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
