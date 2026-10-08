using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Protocol;

/// <summary>
/// 管理者の補助 exe の引数。**補助 exe の境界そのもの**なので、画面の側から試せるようここに置く。
///
///   --pipe PartsLife.Check.&lt;32桁&gt; --parent &lt;pid&gt; [--lang ja|en] smart
///   --pipe ... --parent ... [--lang ja|en] scan --disk &lt;0..63&gt; --mode sample|full [--every &lt;2..65536&gt;]
///
/// ■ 知らない物・余計な物・壊れた物があれば、何もせずに終わる（null）
/// - 知らない引数、同じ引数の2回目、仕事の2つ目、値の無い引数
/// - 数は 10 進の数字だけ（符号・空白・16 進は受けない）。範囲の外も受けない
/// - smart に表面検査の引数（--disk / --mode / --every）を付けたら受けない
/// </summary>
public sealed record HelperArgs(string Pipe, int Parent, string Lang, string Job, int Disk, bool Full, int Every)
{
    public const string PipePrefix = "PartsLife.Check.";

    public static HelperArgs? Parse(IReadOnlyList<string> args)
    {
        string? pipe = null, job = null, lang = null, mode = null;
        int? parent = null, disk = null, every = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "smart" or "scan")
            {
                if (job is not null) return null;
                job = a;
                continue;
            }
            if (i + 1 >= args.Count) return null;          // 値が無い
            string v = args[++i];
            switch (a)
            {
                case "--pipe": if (pipe is not null || !IsPipeName(v)) return null; pipe = v; break;
                case "--parent": if (parent is not null || !Number(v, 1, int.MaxValue, out int p)) return null; parent = p; break;
                case "--lang": if (lang is not null || v is not ("ja" or "en")) return null; lang = v; break;
                case "--disk": if (disk is not null || !Number(v, 0, StorageReader.MaxDisks - 1, out int d)) return null; disk = d; break;
                case "--mode": if (mode is not null || v is not ("sample" or "full")) return null; mode = v; break;
                case "--every": if (every is not null || !Number(v, 2, 65536, out int e)) return null; every = e; break;
                default: return null;
            }
        }
        if (pipe is null || parent is null || job is null) return null;
        if (job == "smart")
        {
            if (disk is not null || mode is not null || every is not null) return null;
            return new HelperArgs(pipe, parent.Value, lang ?? "ja", job, -1, false, 256);
        }
        if (disk is null || mode is null) return null;
        return new HelperArgs(pipe, parent.Value, lang ?? "ja", job, disk.Value, mode == "full", every ?? 256);
    }

    /// <summary>"PartsLife.Check." + 小文字の 16 進 32 桁（HelperSession が Guid の "N" で作る名前）。</summary>
    public static bool IsPipeName(string s) =>
        s.Length == PipePrefix.Length + 32 && s.StartsWith(PipePrefix, StringComparison.Ordinal)
        && s.AsSpan(PipePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool Number(string s, int min, int max, out int value)
    {
        value = 0;
        if (s.Length is 0 or > 10) return false;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return false;
        if (v < min || v > max) return false;
        value = v;
        return true;
    }
}

/// <summary>補助 exe の側のパイプの扱い。</summary>
public static class HelperPipe
{
    public enum Connect { Ok, NoServer, WrongServer }

    /// <summary>
    /// 起動元のパイプに繋ぐ。**繋いだ先のパイプを作ったのが起動元（<paramref name="parentPid"/>）でなければ、
    /// 何も読まずに切る**（同じ名前で先回りした別のプロセスに結果を渡さない）。
    /// </summary>
    public static Connect ConnectToParent(string pipeName, int parentPid, int timeoutMs, out NamedPipeClientStream? pipe)
    {
        pipe = null;
        NamedPipeClientStream p;
        try
        {
            // Asynchronous で開く。同期の handle だと、"stop" を待つ読み取りが
            // 結果の書き込みを塞いでしまう（同じ handle の同期 I/O は順番待ちになる）
            p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            p.Connect(timeoutMs);
        }
        catch (Exception) { return Connect.NoServer; }

        if (!GetNamedPipeServerProcessId(p.SafePipeHandle, out uint server) || server != (uint)parentPid)
        {
            p.Dispose();
            return Connect.WrongServer;
        }
        pipe = p;
        return Connect.Ok;
    }

    /// <summary>
    /// 窓から届く行を読み、<c>stop</c> が来るか、パイプが切れるまで待つ。**ほかの行は何であれ無視する。**
    /// stop なら true、切れたら false。
    /// </summary>
    public static bool WaitForStop(TextReader reader)
    {
        try
        {
            while (reader.ReadLine() is { } line)
                if (HelperMessage.IsStopCommand(line)) return true;
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
