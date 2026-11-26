using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PartsLife.Update.Tests;

/// <summary>最後の版の既読の記録（final-notice.json）の決まり。実際の %LOCALAPPDATA% には触らない（一時フォルダだけ）。</summary>
public class FinalNoticeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pl-final-notice-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); else if (File.Exists(_dir)) File.Delete(_dir); }
        catch (IOException) { }
    }

    [Fact]
    public void Absent_is_not_read()
    {
        Assert.False(new FinalNoticeStore(_dir).IsRead());
    }

    [Fact]
    public void Written_file_is_read_and_has_the_agreed_shape()
    {
        var s = new FinalNoticeStore(_dir);
        Assert.True(s.TryMarkRead(DateTimeOffset.FromUnixTimeSeconds(1790000000), out var err));
        Assert.Null(err);
        Assert.True(s.IsRead());
        Assert.Equal("{\"NoticeId\":\"pcmieru-merge-1\",\"Read\":true,\"ReadAtUnix\":1790000000}", File.ReadAllText(s.FilePath));
        Assert.Equal("final-notice.json", Path.GetFileName(s.FilePath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Other_notice_id_is_not_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FinalNoticeStore.FileName), "{\"NoticeId\":\"something-else\",\"Read\":true,\"ReadAtUnix\":1}");
        Assert.False(new FinalNoticeStore(_dir).IsRead());
    }

    [Theory]
    [InlineData("{\"NoticeId\":\"pcmieru-merge-1\",\"Read\":false,\"ReadAtUnix\":1}")]
    [InlineData("{\"NoticeId\":\"pcmieru-merge-1\"}")]
    [InlineData("{\"NoticeId\":1,\"Read\":true}")]
    [InlineData("[1,2,3]")]
    [InlineData("{ broken")]
    [InlineData("")]
    public void Broken_or_unread_json_is_not_read(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FinalNoticeStore.FileName), json);
        Assert.False(new FinalNoticeStore(_dir).IsRead());
    }

    [Fact]
    public void Unreadable_path_is_not_read()
    {
        // final-notice.json の場所がフォルダになっている → 読めない → 既読ではない
        Directory.CreateDirectory(Path.Combine(_dir, FinalNoticeStore.FileName));
        Assert.False(new FinalNoticeStore(_dir).IsRead());
    }

    [Fact]
    public void Overwrite_replaces_an_old_record()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FinalNoticeStore.FileName), "{\"NoticeId\":\"old\",\"Read\":true}");
        var s = new FinalNoticeStore(_dir);
        Assert.True(s.TryMarkRead(DateTimeOffset.UtcNow, out _));
        Assert.True(s.IsRead());
    }

    [Fact]
    public void Write_failure_when_the_directory_is_a_file()
    {
        File.WriteAllText(_dir, "not a directory");
        var s = new FinalNoticeStore(_dir);
        Assert.False(s.TryMarkRead(DateTimeOffset.UtcNow, out var err));
        Assert.False(string.IsNullOrEmpty(err));
        Assert.False(s.IsRead());
        Assert.True(File.Exists(_dir));   // 触っていない
    }

    [Fact]
    public void Write_failure_after_the_tmp_was_written_cleans_the_tmp_up()
    {
        // 入れ替え先がフォルダ → .tmp は書けるが File.Move が失敗する。.tmp が残らないこと・既読にならないこと
        var target = Path.Combine(_dir, FinalNoticeStore.FileName);
        Directory.CreateDirectory(target);
        var s = new FinalNoticeStore(_dir);
        Assert.False(s.TryMarkRead(DateTimeOffset.UtcNow, out var err));
        Assert.False(string.IsNullOrEmpty(err));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(s.IsRead());
    }

    [Fact]
    public void Page_url_is_the_one_agreed_constant()
    {
        Assert.Equal("https://web-autoai.com/pc-mieru", FinalNoticeStore.PageUrl);
        Assert.Equal("pcmieru-merge-1", FinalNoticeStore.NoticeId);
    }
}

/// <summary>常駐の Mutex の名前（PCミエルの Party と共有。決まりを変えると 2 つが別々の名前を見て、同じ保存を同時に書く）。</summary>
[SupportedOSPlatform("windows")]
public class ResidentMutexTests
{
    private const string FixedSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    // 試験の中で同じ規則を別に書く: SID 文字列（UTF-8）の SHA-256 の先頭 16 桁・小文字
    private static string Expected(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];

    [Fact]
    public void Name_is_the_literal_prefix_plus_the_sid_hash()
    {
        Assert.Equal(@"Global\PCMieru.PartsLife.Resident.v1.", ResidentMutex.NamePrefix);
        Assert.Equal(@"Global\PCMieru.PartsLife.Resident.v1." + Expected(FixedSid), ResidentMutex.NameFor(FixedSid));
        var hash = ResidentMutex.NameFor(FixedSid)[ResidentMutex.NamePrefix.Length..];
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void Another_user_gets_another_name()
    {
        Assert.NotEqual(ResidentMutex.NameFor(FixedSid), ResidentMutex.NameFor(FixedSid + "0"));
    }

    [Fact]
    public void Test_dir_adds_a_lower_cased_dir_hash_and_nothing_else_changes()
    {
        string plain = ResidentMutex.NameFor(FixedSid);
        string withDir = ResidentMutex.NameFor(FixedSid, @"C:\Temp\PL-Test\Data");
        Assert.Equal(plain + "." + Expected(@"c:\temp\pl-test\data"), withDir);
        Assert.Equal(withDir, ResidentMutex.NameFor(FixedSid, @"c:\TEMP\pl-test\DATA"));   // 大文字小文字は同じ
        Assert.NotEqual(withDir, ResidentMutex.NameFor(FixedSid, @"C:\Temp\PL-Test\Other"));
        Assert.Equal(plain, ResidentMutex.NameFor(FixedSid, null));
    }

    [Fact]
    public void Current_name_follows_the_same_rule_for_the_real_sid()
    {
        Assert.Equal(ResidentMutex.NameFor(ResidentMutex.CurrentSid()), ResidentMutex.CurrentName());
        Assert.StartsWith(ResidentMutex.NamePrefix, ResidentMutex.CurrentName());
    }

    [Fact]
    public void Second_taker_is_refused_until_the_first_lets_go()
    {
        // 本物の名前には触らない（普段のパーツぐらし・PCミエルを止めない）。この試験だけの名前で、取る・取れない・放したら取れる
        string name = @"Local\PartsLife.Test.Resident." + Guid.NewGuid().ToString("N");
        var first = ResidentMutex.TryAcquire(name);
        Assert.NotNull(first);
        try
        {
            // 別のスレッドから（同じスレッドなら Mutex は再入できてしまう）
            Assert.Null(OnOtherThread(() => ResidentMutex.TryAcquire(name)));
        }
        finally
        {
            first!.Dispose();
        }
        var again = OnOtherThread(() => ResidentMutex.TryAcquire(name));
        Assert.NotNull(again);
        OnOtherThread(() => { again!.Dispose(); return 0; });
    }

    private static T OnOtherThread<T>(Func<T> f)
    {
        T? r = default;
        Exception? ex = null;
        var t = new Thread(() => { try { r = f(); } catch (Exception e) { ex = e; } });
        t.Start();
        t.Join();
        if (ex is not null) throw ex;
        return r!;
    }
}

/// <summary>
/// 「通常の運転では更新を確かめに行かない」の歯止め（ソースの文字列を固定する試験）。
/// 本体は WPF で、ここからは起こせない。そこで、確かめに行く経路（タイマーと Updater の呼び出し）が
/// 本体のソースに**書かれていないこと**を見る。足し戻されたらこの試験が落ちる。
/// </summary>
public class NoUpdateCheckWiringTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PartsLife.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PartsLife.slnx が見つからない");
    }

    [Fact]
    public void Update_controller_has_no_check_timers_and_no_check_call()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PartsLife", "Updates", "UpdateController.cs"));
        foreach (var banned in new[] { "RunCheck", "CheckAsync", "new Updater(", "_first", "_poll", "_idle", "UpdateSchedule.FirstDelay", "UpdateSchedule.Poll" })
            Assert.DoesNotContain(banned, src);
        // 残してある物（入れ替えの確かめ）は、まだある
        Assert.Contains("_healthy", src);
        Assert.Contains("ConfirmStarted", src);
        Assert.Contains("OnAppClosing", src);
    }

    [Fact]
    public void No_source_of_the_app_calls_the_updater_check()
    {
        string dir = Path.Combine(RepoRoot(), "src", "PartsLife");
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("CheckAsync(", text);
            Assert.DoesNotContain("new Updater(", text);
        }
    }

    [Fact]
    public void Shown_after_the_confirmation_is_written_and_resident_mutex_taken_before_the_window()
    {
        string root = RepoRoot();
        string ctl = File.ReadAllText(Path.Combine(root, "src", "PartsLife", "Updates", "UpdateController.cs"));
        int confirmed = ctl.IndexOf("rec.Confirmed = true;", ctl.IndexOf("private void ConfirmStarted", StringComparison.Ordinal), StringComparison.Ordinal);
        int shown = ctl.IndexOf("FinalNoticeUi.ShowIfUnread", confirmed, StringComparison.Ordinal);
        Assert.True(confirmed > 0 && shown > confirmed, "Confirmed を書いたあとで案内を出す");

        string app = File.ReadAllText(Path.Combine(root, "src", "PartsLife", "App.xaml.cs"));
        int start = app.IndexOf("private void StartMain", StringComparison.Ordinal);
        int take = app.IndexOf("ResidentMutex.TryAcquire", start, StringComparison.Ordinal);
        int window = app.IndexOf("new MainWindow(state)", start, StringComparison.Ordinal);
        Assert.True(start > 0 && take > start && window > take, "MainWindow を作る前に常駐の Mutex を取る");
    }
}
