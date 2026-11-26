using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PartsLife.Update;

/// <summary>
/// 常駐の所有者を一人にする名前つき Mutex（DESIGN-C 3.2）。**PCミエルの Party と共有する**ので、名前の決まりを変えない:
/// <c>Global\PCMieru.PartsLife.Resident.v1.&lt;SID のハッシュ&gt;</c>。ハッシュは、利用者の SID 文字列（UTF-8）の SHA-256 の
/// 先頭 16 桁（小文字）。ACL は当該利用者と LocalSystem だけ（FullControl）。
///
///   - 取れなかった（ほかが握っている）→ null。このアプリは短いお知らせだけ出して終わる（保存にも swap.json にも書かない）
///   - ACL のせいで開けない（UnauthorizedAccessException）→ 別の誰かの物として、握られている扱い
///   - 握っていた側が落ちた（Abandoned）→ 引き継ぐ（持ち主はもういない）
/// Mutex は握ったスレッドでしか放せない。このアプリでは画面のスレッドで握り、窓が続く間ずっと持ち、同じスレッドで放す。
///
/// Debug ビルドで試験用の保存の置き場があるときだけ、名前の末尾に <c>.</c> + 置き場（小文字）の SHA-256 の先頭 16 桁を付ける
/// （通しの確かめが、普段使っているパーツぐらし・PCミエルの Party と衝突しないように。UpdateLock と同じ規則）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResidentMutex : IDisposable
{
    public const string NamePrefix = @"Global\PCMieru.PartsLife.Resident.v1.";

    private Mutex? _mutex;

    private ResidentMutex(Mutex mutex) => _mutex = mutex;

    /// <summary>文字列の SHA-256（UTF-8）の先頭 16 桁（小文字）。PartyMutexNames.SidHash と同じ規則。</summary>
    public static string SidHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>試験用の置き場の目印。<c>.</c> + 小文字にした置き場の SHA-256 の先頭 16 桁。</summary>
    public static string TestSuffix(string testDir) => "." + SidHash(testDir.ToLowerInvariant());

    public static string NameFor(string sid, string? testDir = null) =>
        NamePrefix + SidHash(sid) + (testDir is null ? "" : TestSuffix(testDir));

    public static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("the current user has no SID");
    }

    public static string CurrentName(string? testDir = null) => NameFor(CurrentSid(), testDir);

    /// <summary>握る。ほかが握っていれば null。</summary>
    public static ResidentMutex? TryAcquire(string name)
    {
        var security = new MutexSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));

        Mutex mutex;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, name, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new ResidentMutex(mutex);
    }

    /// <summary>放す。握ったスレッドで呼ぶ。</summary>
    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null) return;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { }   // 別のスレッドから呼ばれた（放せない）。プロセスが終われば消える
        finally { mutex.Dispose(); }
    }
}
