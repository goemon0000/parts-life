namespace PartsLife.Update.Tests;

/// <summary>旧い常駐の判定。**偽の一覧だけ**で回す（本物のプロセスは一覧にしない・Windows でなくても回る）。</summary>
public class LegacyResidentTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";
    private static readonly DateTime SelfStart = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Version Old = new(0, 7, 4, 0);
    private static readonly Version New = new(0, 8, 0, 0);

    private static ProcessFact P(int pid, string name = "PartsLife", string? sid = Me, Version? v = null, DateTime? start = null) =>
        new(pid, name, sid, v, start);

    private static ProcessFact? Find(int? exclude, params ProcessFact[] list) => LegacyResident.Find(list, 100, exclude, Me, SelfStart);

    [Fact]
    public void Old_version_of_the_same_user_is_found()
    {
        var f = Find(null, P(200, v: Old, start: SelfStart.AddMinutes(-5)));
        Assert.Equal(200, f?.Pid);
    }

    [Fact]
    public void Old_version_counts_even_if_it_started_after_us()
    {
        Assert.Equal(200, Find(null, P(200, v: Old, start: SelfStart.AddMinutes(1)))?.Pid);
    }

    [Fact]
    public void Version_0_8_0_or_later_is_not_old()
    {
        Assert.Null(Find(null, P(200, v: New, start: SelfStart.AddMinutes(-5)), P(201, v: new Version(0, 9, 1, 0))));
    }

    [Fact]
    public void Party_with_an_old_version_is_found_too_and_names_are_case_insensitive()
    {
        Assert.Equal(300, Find(null, P(300, name: "partsparty", v: Old))?.Pid);
        Assert.Equal(301, Find(null, P(301, name: "PARTSLIFE.EXE", v: Old))?.Pid);
    }

    [Fact]
    public void Other_names_and_other_users_and_ourselves_are_not_counted()
    {
        Assert.Null(Find(null,
            P(200, name: "notepad", v: Old),
            P(201, name: "PartsLifeHelper", v: Old),
            P(202, sid: Other, v: Old),
            P(100, v: Old)));   // 自分
    }

    [Fact]
    public void Parent_pid_of_an_updated_from_child_is_excluded()
    {
        var parent = P(150, v: Old, start: SelfStart.AddSeconds(-3));
        Assert.Null(Find(150, parent));
        Assert.Equal(150, Find(null, parent)?.Pid);   // 子でなければ数える
        Assert.Equal(151, Find(150, parent, P(151, v: Old))?.Pid);   // 親以外の旧い常駐は数える
    }

    [Fact]
    public void Unreadable_version_counts_only_if_it_started_before_us()
    {
        Assert.Equal(200, Find(null, P(200, v: null, start: SelfStart.AddSeconds(-1)))?.Pid);
        Assert.Null(Find(null, P(201, v: null, start: SelfStart.AddSeconds(1))));   // 後から来た側は Mutex で負ける。両方終わらない
        Assert.Null(Find(null, P(202, v: null, start: SelfStart)));
    }

    [Fact]
    public void Unreadable_owner_counts_only_if_it_started_before_us()
    {
        Assert.Equal(200, Find(null, P(200, sid: null, v: Old, start: SelfStart.AddMinutes(-2)))?.Pid);
        Assert.Null(Find(null, P(201, sid: null, v: Old, start: SelfStart.AddMinutes(2))));
        Assert.Equal(202, Find(null, P(202, sid: null, v: null, start: SelfStart.AddMinutes(-2)))?.Pid);
    }

    [Fact]
    public void Unreadable_owner_with_a_new_version_is_not_old()
    {
        Assert.Null(Find(null, P(200, sid: null, v: New, start: SelfStart.AddMinutes(-2))));
    }

    [Fact]
    public void Unknown_start_time_counts_as_earlier_when_unreadable()
    {
        Assert.Equal(200, Find(null, P(200, v: null, start: null))?.Pid);
        Assert.Equal(201, LegacyResident.Find(new[] { P(201, v: null, start: SelfStart.AddHours(1)) }, 100, null, Me, null)?.Pid);
    }

    [Fact]
    public void Empty_list_finds_nothing()
    {
        Assert.Null(Find(null));
    }

    [Fact]
    public void Resident_names()
    {
        Assert.True(LegacyResident.IsResidentName("PartsLife"));
        Assert.True(LegacyResident.IsResidentName("PartsParty.exe"));
        Assert.False(LegacyResident.IsResidentName("PartsLife2"));
        Assert.False(LegacyResident.IsResidentName(""));
    }
}

/// <summary>「起動できた」の書き込みの再試行。記録の保存だけを差し替える（本物のプロセス・レジストリには触らない）。</summary>
public class SwapConfirmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pl-swap-confirm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private sealed class FlakyStore(string root, int failFirst) : UpdateStore(root)
    {
        public int Calls;
        public override bool SaveSwap(SwapRecord r)
        {
            Calls++;
            return Calls > failFirst && base.SaveSwap(r);
        }
    }

    private static SwapRecord Rec(string to = "0.8.0") =>
        new() { From = "0.7.4", To = to, Files = ["PartsLife.exe"], OldPid = 1234, Launched = true };

    private FlakyStore StoreWith(int failFirst, SwapRecord? rec)
    {
        Directory.CreateDirectory(_dir);
        if (rec is not null) Assert.True(new UpdateStore(_dir).SaveSwap(rec));
        return new FlakyStore(_dir, failFirst);
    }

    [Fact]
    public void First_try_writes_and_does_not_wait()
    {
        var s = StoreWith(0, Rec());
        var waits = new List<TimeSpan>();
        Assert.Equal(ConfirmOutcome.Confirmed, SwapConfirm.TryConfirm(s, "0.8.0", out var rec, sleep: waits.Add));
        Assert.True(rec!.Confirmed);
        Assert.True(s.LoadSwap()!.Confirmed);
        Assert.Equal(1, s.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public void Transient_failures_are_retried_200ms_apart_until_it_writes()
    {
        var s = StoreWith(3, Rec());
        var waits = new List<TimeSpan>();
        Assert.Equal(ConfirmOutcome.Confirmed, SwapConfirm.TryConfirm(s, "0.8.0", out _, sleep: waits.Add));
        Assert.Equal(4, s.Calls);
        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromMilliseconds(200), w));
        Assert.True(s.LoadSwap()!.Confirmed);
    }

    [Fact]
    public void Five_failures_in_a_row_give_up_and_leave_it_unconfirmed()
    {
        var s = StoreWith(99, Rec());
        var waits = new List<TimeSpan>();
        Assert.Equal(ConfirmOutcome.Failed, SwapConfirm.TryConfirm(s, "0.8.0", out var rec, sleep: waits.Add));
        Assert.Null(rec);
        Assert.Equal(SwapConfirm.Tries, s.Calls);
        Assert.Equal(SwapConfirm.Tries - 1, waits.Count);
        Assert.False(s.LoadSwap()!.Confirmed);
        // 全部で 1 秒あまり。見張りの 120 秒に十分収まる
        Assert.True(waits.Aggregate(TimeSpan.Zero, (a, b) => a + b) < SwapBoot.WatchTimeout);
    }

    [Fact]
    public void No_record_or_another_target_is_not_a_pending_swap()
    {
        var none = StoreWith(0, null);
        Assert.Equal(ConfirmOutcome.NoPendingSwap, SwapConfirm.TryConfirm(none, "0.8.0", out var r1, sleep: _ => { }));
        Assert.Null(r1);
        Assert.Equal(0, none.Calls);

        var other = StoreWith(0, Rec(to: "0.9.0"));
        Assert.Equal(ConfirmOutcome.NoPendingSwap, SwapConfirm.TryConfirm(other, "0.8.0", out _, sleep: _ => { }));
        Assert.Equal(0, other.Calls);
        Assert.False(other.LoadSwap()!.Confirmed);
    }

    [Fact]
    public void Already_confirmed_is_confirmed_without_writing()
    {
        var rec = Rec();
        rec.Confirmed = true;
        var s = StoreWith(0, rec);
        Assert.Equal(ConfirmOutcome.Confirmed, SwapConfirm.TryConfirm(s, "0.8.0", out _, sleep: _ => { }));
        Assert.Equal(0, s.Calls);
    }

    [Fact]
    public void Unreadable_record_is_a_failure_not_a_missing_one()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "swap.json"), "{ broken");
        var s = new FlakyStore(_dir, 0);
        var waits = new List<TimeSpan>();
        Assert.Equal(ConfirmOutcome.Failed, SwapConfirm.TryConfirm(s, "0.8.0", out _, sleep: waits.Add));
        Assert.Equal(SwapConfirm.Tries - 1, waits.Count);
        Assert.Equal(0, s.Calls);
    }
}
