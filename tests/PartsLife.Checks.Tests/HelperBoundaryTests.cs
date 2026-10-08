using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PartsLife.Checks.Protocol;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 管理者の補助 exe の境界: 引数・窓から送ってよい物・パイプの相手の確かめ方。
/// **昇格もしないし、ディスクも開かない。** 補助 exe の代わりに、試験のプロセス自身や
/// 何もしない子プロセスを相手にして、手順だけを確かめる。
/// </summary>
public class HelperArgsTests
{
    private const string Pipe = "PartsLife.Check.0123456789abcdef0123456789abcdef";

    private static HelperArgs? P(string line) => HelperArgs.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void What_the_window_sends_is_accepted()
    {
        var smart = P($"--pipe {Pipe} --parent 1234 --lang en smart");
        Assert.NotNull(smart);
        Assert.Equal(("smart", 1234, "en"), (smart!.Job, smart.Parent, smart.Lang));

        var scan = P($"--pipe {Pipe} --parent 1234 --lang ja scan --disk 1 --mode sample --every 256");
        Assert.NotNull(scan);
        Assert.Equal(("scan", 1, false, 256), (scan!.Job, scan.Disk, scan.Full, scan.Every));

        var full = P($"--pipe {Pipe} --parent 9 scan --disk 63 --mode full");
        Assert.Equal((63, true, 256, "ja"), (full!.Disk, full.Full, full.Every, full.Lang));
    }

    [Theory]
    // 知らない引数・知らない仕事
    [InlineData("--pipe {P} --parent 1 smart --verbose x")]
    [InlineData("--pipe {P} --parent 1 format")]
    [InlineData("--pipe {P} --parent 1 smart write")]
    [InlineData("--pipe {P} --parent 1 --write 1 smart")]
    // 余計な物: 仕事の2つ目、同じ引数の2回目、smart に表面検査の引数
    [InlineData("--pipe {P} --parent 1 smart smart")]
    [InlineData("--pipe {P} --parent 1 smart scan --disk 0 --mode sample")]
    [InlineData("--pipe {P} --parent 1 --parent 2 smart")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0 --disk 1 --mode sample")]
    [InlineData("--pipe {P} --parent 1 --lang ja --lang en smart")]
    [InlineData("--pipe {P} --parent 1 smart --disk 0")]
    [InlineData("--pipe {P} --parent 1 smart --mode full")]
    [InlineData("--pipe {P} --parent 1 smart --every 4")]
    // 壊れた --disk
    [InlineData("--pipe {P} --parent 1 scan --disk x --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 1x --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk +1 --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0x1 --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 99999999999 --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --mode sample --disk")]
    // 範囲の外の --disk / --every
    [InlineData("--pipe {P} --parent 1 scan --disk -1 --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 64 --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0 --mode sample --every 1")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0 --mode sample --every 65537")]
    // 足りない物
    [InlineData("--pipe {P} --parent 1 scan --mode sample")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0")]
    [InlineData("--pipe {P} smart")]
    [InlineData("--parent 1 smart")]
    [InlineData("--pipe {P} --parent 1")]
    [InlineData("")]
    // 壊れた値
    [InlineData("--pipe {P} --parent 0 smart")]
    [InlineData("--pipe {P} --parent -5 smart")]
    [InlineData("--pipe {P} --parent 1 --lang de smart")]
    [InlineData("--pipe {P} --parent 1 scan --disk 0 --mode write")]
    [InlineData("--pipe PartsLife.Check.0123 --parent 1 smart")]
    [InlineData("--pipe PartsLife.Check.0123456789ABCDEF0123456789ABCDEF --parent 1 smart")]
    [InlineData("--pipe ..\\PartsLife.Check.0123456789abcdef0123456789abcdef --parent 1 smart")]
    [InlineData("--pipe Other.0123456789abcdef0123456789abcdef --parent 1 smart")]
    public void Unknown_extra_malformed_or_out_of_range_arguments_do_nothing(string line)
    {
        Assert.Null(P(line.Replace("{P}", Pipe)));
    }

    [Fact]
    public void Disk_bounds_are_exactly_0_to_63()
    {
        Assert.NotNull(P($"--pipe {Pipe} --parent 1 scan --disk 0 --mode sample"));
        Assert.NotNull(P($"--pipe {Pipe} --parent 1 scan --disk 63 --mode sample"));
        Assert.Null(P($"--pipe {Pipe} --parent 1 scan --disk 64 --mode sample"));
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("stop\r", true)]
    [InlineData("  stop  ", true)]
    [InlineData("STOP", false)]
    [InlineData("stop now", false)]
    [InlineData("scan --disk 0 --mode full", false)]
    [InlineData("{\"type\":\"stop\"}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_stop_is_accepted_back(string? line, bool isStop)
    {
        Assert.Equal(isStop, HelperMessage.IsStopCommand(line));
    }

    [Fact]
    public void Helper_ignores_everything_but_stop_and_ends_on_disconnect()
    {
        Assert.True(HelperPipe.WaitForStop(new StringReader("scan --disk 0 --mode full\n{\"type\":\"scan\"}\nSTOP\nstop\nnever read\n")));
        Assert.False(HelperPipe.WaitForStop(new StringReader("smart\nscan --disk 1\n")));
        Assert.False(HelperPipe.WaitForStop(new StringReader("")));
    }
}

public class HelperPipeHandshakeTests
{
    private static string PipeNameFrom(string args)
    {
        var parts = args.Split(' ');
        return parts[Array.IndexOf(parts, "--pipe") + 1];
    }

    /// <summary>何もしない子プロセス（標準入力を待つだけ）。終わりに必ず止める。</summary>
    private static Process StartIdleChild() => Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
    })!;

    private static void Stop(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    private static Task ClientWrites(string pipeName, string text) => Task.Run(() =>
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            c.Connect(5000);
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Write(bytes, 0, bytes.Length);
            c.Flush();
            Thread.Sleep(300);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    });

    [Fact]
    public async Task Genuine_client_is_accepted()
    {
        Task? client = null;
        var got = new List<HelperMessage>();
        var (outcome, detail) = await HelperSession.RunCoreAsync(args =>
        {
            client = ClientWrites(PipeNameFrom(args), "{\"type\":\"hello\",\"pid\":1}\n{\"type\":\"disks\",\"diskReports\":[]}\n{\"type\":\"done\"}\n");
            return Process.GetCurrentProcess();     // 「起動した補助 exe」= 繋ぎに来る試験のプロセス自身
        }, "smart", got.Add, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        await client!;
        Assert.Equal(HelperOutcome.Completed, outcome);
        Assert.Null(detail);
        Assert.Equal(new[] { HelperMessage.Hello, HelperMessage.Disks }, got.Select(m => m.Type));
    }

    [Fact]
    public async Task Impostor_client_is_rejected_before_anything_is_read()
    {
        Process? child = null;
        Task? impostor = null;
        var got = new List<HelperMessage>();
        try
        {
            var (outcome, detail) = await HelperSession.RunCoreAsync(args =>
            {
                // 起動したのは子プロセス。ところが繋ぎに来るのは別のプロセス（この試験のプロセス）
                child = StartIdleChild();
                impostor = ClientWrites(PipeNameFrom(args), "{\"type\":\"disks\",\"diskReports\":[]}\n{\"type\":\"done\"}\n");
                return Process.GetProcessById(child.Id);
            }, "smart", got.Add, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(HelperOutcome.Failed, outcome);
            Assert.Equal("unexpected pipe client", detail);
            Assert.Empty(got);
            await impostor!;
        }
        finally { Stop(child); }
    }

    [Fact]
    public void Helper_refuses_a_pipe_not_created_by_its_parent()
    {
        string name = HelperArgs.PipePrefix + Guid.NewGuid().ToString("N");
        Process? child = null;
        try
        {
            child = StartIdleChild();
            // 同じ名前のパイプを、起動元ではないプロセス（この試験のプロセス）が先に作った
            using var impostor = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = impostor.WaitForConnectionAsync();
            var r = HelperPipe.ConnectToParent(name, parentPid: child.Id, 5000, out var pipe);
            Assert.Equal(HelperPipe.Connect.WrongServer, r);
            Assert.Null(pipe);
        }
        finally { Stop(child); }
    }

    [Fact]
    public async Task Helper_accepts_the_pipe_its_parent_created()
    {
        string name = HelperArgs.PipePrefix + Guid.NewGuid().ToString("N");
        using var server = HelperSession.CreatePipe(name);
        var accept = server.WaitForConnectionAsync();
        var r = HelperPipe.ConnectToParent(name, Environment.ProcessId, 5000, out var pipe);
        using (pipe)
        {
            Assert.Equal(HelperPipe.Connect.Ok, r);
            Assert.NotNull(pipe);
            await accept.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(HelperPipe.Connect.NoServer,
            HelperPipe.ConnectToParent(HelperArgs.PipePrefix + Guid.NewGuid().ToString("N"), Environment.ProcessId, 200, out _));
    }

    [Fact]
    public void Pipe_is_open_only_to_this_user_and_administrators()
    {
        string name = HelperArgs.PipePrefix + Guid.NewGuid().ToString("N");
        using var server = HelperSession.CreatePipe(name);
        var me = WindowsIdentity.GetCurrent().User!;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.NotEmpty(rules);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(admins), $"unexpected {r.IdentityReference}");
        });
        // Administrators は読み書きだけ。権限の書き換え・所有者の取得はできない
        var a = rules.Where(r => r.IdentityReference.Equals(admins)).Aggregate((PipeAccessRights)0, (x, r) => x | r.PipeAccessRights);
        Assert.Equal((PipeAccessRights)0, a & (PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership));
        foreach (var everyone in new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid,
                                         WellKnownSidType.AnonymousSid, WellKnownSidType.BuiltinUsersSid })
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(everyone, null)));
    }

    /// <summary>
    /// 先回りして同じ名前のパイプを作られていたら、その後ろに並ばずに失敗する。
    /// （FirstPipeInstance と、instance を 1 本に限っていることの二重の歯止め。どちらか片方でも失敗する）
    /// </summary>
    [Fact]
    public void Pipe_name_already_taken_by_someone_else_is_not_joined()
    {
        string name = HelperArgs.PipePrefix + Guid.NewGuid().ToString("N");
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var ex = Record.Exception(() => HelperSession.CreatePipe(name).Dispose());
        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name ?? "no exception");
    }
}
