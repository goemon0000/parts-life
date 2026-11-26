using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace PartsLife.Update;

/// <summary>他のプロセスについて、読めた事実。読めなかった物は null。</summary>
/// <param name="OwnerSid">そのプロセスを動かしている利用者の SID。</param>
/// <param name="FileVersion">実行ファイルのファイル バージョン。</param>
/// <param name="StartUtc">起動した時刻（UTC）。</param>
public sealed record ProcessFact(int Pid, string Name, string? OwnerSid, Version? FileVersion, DateTime? StartUtc);

/// <summary>
/// 旧い常駐（0.8.0 より前の PartsLife / PartsParty）が動いていないかの決まり。**純粋な関数**（プロセスの一覧は呼ぶ側が渡す。試験は偽の一覧だけで回す）。
///
/// 0.7.4 以前は常駐の Mutex を握らない。0.8.0 が Mutex だけを見て起動すると、旧い常駐と同じ保存を同時に書いてしまう。
/// そこで Mutex を握ったあと、同じ利用者の旧い常駐を探し、居たら放して終わる。
///   - 名前が PartsLife / PartsParty でない物・別の利用者の物・自分自身・（入れ替えで起こされた子なら）親の PID は数えない
///   - ファイル バージョンが読めて 0.8.0 以上なら、旧くない（0.8.0 以上は Mutex を握る。握れなかった側が先に終わる）
///   - 読めて 0.8.0 未満なら、旧い常駐
///   - **所有者かバージョンが読めない物**は、自分より先に起動していたときだけ数える（安全な側）。
///     同時に起動した 0.8.0 同士が、互いを旧い常駐と見て両方終わる、をしないため（後から来た側は Mutex で負ける）。
///     起動時刻が読めない物は、先と見なして数える
/// </summary>
public static class LegacyResident
{
    /// <summary>常駐の Mutex を握るようになった最初の版。</summary>
    public static readonly Version FirstMutexVersion = new(0, 8, 0);

    public static bool IsResidentName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("PartsLife", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PartsParty", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>旧い常駐が居れば、その 1 つ（なければ null）。</summary>
    /// <param name="processes">呼ぶ側が渡す一覧（試験では偽物）。</param>
    /// <param name="selfPid">この（いま起動している）プロセス。</param>
    /// <param name="excludePid">入れ替えで起こされた子のとき、親（<see cref="SwapRecord.OldPid"/>）。</param>
    /// <param name="selfSid">この利用者の SID。</param>
    /// <param name="selfStartUtc">この起動の時刻。</param>
    public static ProcessFact? Find(IEnumerable<ProcessFact> processes, int selfPid, int? excludePid, string selfSid, DateTime? selfStartUtc)
    {
        foreach (var p in processes)
        {
            if (!IsResidentName(p.Name)) continue;
            if (p.Pid == selfPid || (excludePid is { } ex && p.Pid == ex)) continue;
            if (p.OwnerSid is not null && !string.Equals(p.OwnerSid, selfSid, StringComparison.OrdinalIgnoreCase)) continue;   // 別の利用者

            if (p.FileVersion is { } v)
            {
                if (v >= FirstMutexVersion) continue;   // 0.8.0 以上は Mutex を握る側
                if (p.OwnerSid is not null) return p;   // 同じ利用者の旧い常駐
            }
            // ここへ来るのは、所有者かバージョンが読めない物。先に起動していたときだけ数える
            if (p.StartUtc is not { } started || selfStartUtc is not { } mine || started < mine) return p;
        }
        return null;
    }

    /// <summary>実際のプロセスを見て探す（Windows）。<paramref name="excludePid"/> は <see cref="Find"/> と同じ。</summary>
    [SupportedOSPlatform("windows")]
    public static ProcessFact? FindRunning(int? excludePid)
    {
        string sid = ResidentMutex.CurrentSid();
        DateTime? mine = null;
        using (var self = Process.GetCurrentProcess())
        {
            try { mine = self.StartTime.ToUniversalTime(); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            return Find(ProcessScanner.List(), self.Id, excludePid, sid, mine);
        }
    }
}

/// <summary>PartsLife / PartsParty という名前のプロセスの事実を集める（Windows）。読めない項目は null のまま。決まりは <see cref="LegacyResident"/>。</summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessScanner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static List<ProcessFact> List()
    {
        var list = new List<ProcessFact>();
        foreach (var name in new[] { "PartsLife", "PartsParty" })
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { continue; }
            foreach (var p in found)
            {
                using (p) list.Add(Describe(p));
            }
        }
        return list;
    }

    private static ProcessFact Describe(Process p)
    {
        DateTime? start = null;
        try { start = p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }

        Version? version = null;
        try
        {
            string? file = p.MainModule?.FileName;
            if (file is not null)
            {
                var vi = FileVersionInfo.GetVersionInfo(file);
                version = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException or IOException) { }

        return new ProcessFact(p.Id, p.ProcessName, OwnerSid(p.Id), version, start);
    }

    private static string? OwnerSid(int pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or SecurityException)
            {
                return null;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
