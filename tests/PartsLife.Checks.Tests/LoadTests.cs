using System.Diagnostics;
using PartsLife.Checks.Load;

namespace PartsLife.Checks.Tests;

public class PatternTests
{
    [Theory]
    [InlineData(PatternKind.Address)]
    [InlineData(PatternKind.AddressInverted)]
    [InlineData(PatternKind.Checker)]
    [InlineData(PatternKind.Random)]
    public void Fill_then_verify_finds_no_mismatch(PatternKind kind)
    {
        var buf = new ulong[10_000];
        Patterns.Fill(buf, kind, 0xDEADBEEF, 1_000_000);
        var v = Patterns.Verify(buf, kind, 0xDEADBEEF, 1_000_000);
        Assert.Equal(0, v.Mismatches);
        Assert.Empty(v.First);
    }

    [Theory]
    [InlineData(PatternKind.Address)]
    [InlineData(PatternKind.Random)]
    public void A_single_flipped_bit_is_found_with_its_location(PatternKind kind)
    {
        var buf = new ulong[4096];
        Patterns.Fill(buf, kind, 42, 8192);
        ulong original = buf[1234];
        buf[1234] ^= 1UL << 17;      // 1 ビットだけ化けさせる
        var v = Patterns.Verify(buf, kind, 42, 8192);
        Assert.Equal(1, v.Mismatches);
        var m = Assert.Single(v.First);
        Assert.Equal(8192 + 1234, m.Index);
        Assert.Equal(original, m.Expected);
        Assert.Equal(original ^ (1UL << 17), m.Actual);
        Assert.Equal(1, m.FlippedBits);
    }

    [Fact]
    public void Verify_counts_every_mismatch_but_keeps_only_the_first_few()
    {
        var buf = new ulong[1000];
        Patterns.Fill(buf, PatternKind.Checker, 0, 0);
        for (int i = 0; i < 100; i++) buf[i * 10] = 0;
        var v = Patterns.Verify(buf, PatternKind.Checker, 0, 0, keep: 8);
        Assert.Equal(100, v.Mismatches);
        Assert.Equal(8, v.First.Count);
    }

    [Fact]
    public void Wrong_seed_is_detected_everywhere()
    {
        // 別の場所に書けてしまう故障（番地化け）の代わり: 違う seed で照合すると全部ずれる
        var buf = new ulong[512];
        Patterns.Fill(buf, PatternKind.Address, 1, 0);
        Assert.Equal(512, Patterns.Verify(buf, PatternKind.Address, 2, 0).Mismatches);
    }

    [Fact]
    public void Values_depend_only_on_index_and_seed()
    {
        var a = new ulong[100];
        var b = new ulong[50];
        Patterns.Fill(a, PatternKind.Random, 7, 0);
        Patterns.Fill(b, PatternKind.Random, 7, 50);
        Assert.Equal(a[50..], b);
    }
}

public class MemoryTestTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Plan_never_exceeds_half_of_available_and_keeps_a_floor()
    {
        // 64GB 機で空き 40GB: 既定 25% = 10GB
        Assert.Equal(10 * GB, MemoryTest.PlanBytes(40 * GB, 64 * GB, 0.25, null));
        // 0.9 と頼まれても 50% まで
        Assert.Equal(20 * GB, MemoryTest.PlanBytes(40 * GB, 64 * GB, 0.9, null));
        // 直接指定でも 50% を超えない
        Assert.Equal(20 * GB, MemoryTest.PlanBytes(40 * GB, 64 * GB, 0.25, 30 * GB));
        // 空きが少ないときは「残り 1GB か搭載量の 10%」を割らない
        long plan = MemoryTest.PlanBytes(8 * GB, 64 * GB, 0.5, null);
        Assert.True(8 * GB - plan >= 64 * GB / 10, $"left {(8 * GB - plan) / GB} GB");
        // 空きがほぼ無ければ 0（行わない）
        Assert.Equal(0, MemoryTest.PlanBytes(1 * GB, 16 * GB, 0.25, null));
    }

    [Fact]
    public void Plan_is_a_multiple_of_the_block_size()
    {
        long plan = MemoryTest.PlanBytes(33 * GB + 12345, 64 * GB, 0.25, null);
        Assert.Equal(0, plan % MemoryTest.BlockBytes);
    }

    [Fact]
    public void Short_run_on_a_small_region_finds_no_mismatch()
    {
        var r = MemoryTest.Run(new MemoryTestOptions { Bytes = 2 * MemoryTest.BlockBytes, Duration = TimeSpan.FromSeconds(2), Threads = 2 },
                               null, CancellationToken.None);
        Assert.Null(r.AbortReason);
        Assert.Equal(2 * MemoryTest.BlockBytes, r.BytesTested);
        Assert.Equal(0, r.Mismatches);
        Assert.True(r.Passes >= 1, $"passes={r.Passes}");
        Assert.True(r.WriteMBps > 0 && r.ReadMBps > 0);
        Assert.Equal(Verdict.Ok, MemoryTest.Judge(r)[0].Verdict);
    }

    [Fact]
    public async Task Cancel_stops_promptly_and_frees_the_region()
    {
        using var cts = new CancellationTokenSource();
        var task = MemoryTest.RunAsync(new MemoryTestOptions { Bytes = 4 * MemoryTest.BlockBytes, Duration = TimeSpan.FromMinutes(5), Threads = 2 },
                                       null, cts.Token);
        await Task.Delay(500);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms to stop");
        Assert.True(r.Stopped);
        Assert.Equal(0, r.Mismatches);
    }

    [Fact]
    public void Mismatch_is_reported_plainly_as_serious()
    {
        var r = new MemoryTestResult { BytesTested = MemoryTest.BlockBytes, Passes = 1, Mismatches = 3, TotalPhys = 64L << 30 };
        r.FirstMismatches.Add((PatternKind.Random, new Mismatch(10, 0xFF, 0xFE)));
        var f = MemoryTest.Judge(r)[0];
        Assert.Equal(Verdict.Check, f.Verdict);
        Assert.Contains("深刻", f.Reason);
        Assert.Contains("1 ビット違い", f.Reason);
    }
}

public class CpuLoadTestTests
{
    [Fact]
    public void Chunk_is_deterministic()
    {
        Assert.Equal(CpuLoadTest.Chunk(123), CpuLoadTest.Chunk(123));
        Assert.NotEqual(CpuLoadTest.Chunk(123), CpuLoadTest.Chunk(124));
    }

    [Fact]
    public async Task Stop_works_instantly()
    {
        using var cts = new CancellationTokenSource();
        var task = CpuLoadTest.RunAsync(new CpuLoadOptions { Duration = TimeSpan.FromMinutes(5), Threads = 2 }, null, cts.Token);
        await Task.Delay(1300);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms to stop");
        Assert.True(r.Stopped);
        Assert.Equal(0, r.ComputeErrors);
        Assert.True(r.Chunks > 0);
    }

    [Fact]
    public void Already_cancelled_token_returns_without_running_long()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        var r = CpuLoadTest.Run(new CpuLoadOptions { Duration = TimeSpan.FromMinutes(1), Threads = 1 }, null, cts.Token);
        Assert.True(r.Stopped);
        // 1 分は走らないことを見る。共有の CI 機や重い手元では立ち上がりだけで 1 秒を超えることがあった
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void Duration_ends_the_test_and_reports_each_second()
    {
        var ticks = new List<CpuTick>();
        var r = CpuLoadTest.Run(new CpuLoadOptions { Duration = TimeSpan.FromSeconds(3), Threads = 2 },
                                new SyncProgress<CpuTick>(ticks.Add), CancellationToken.None);
        Assert.False(r.Stopped);
        // 終わりの瞬間に 1 回多く数えることがある（共有の CI 機は遅れやすい。v0.7.2 のリリースで 4 が出た）
        Assert.InRange(r.Ticks.Count, 2, 4);
        Assert.Equal(r.Ticks.Count, ticks.Count);
        Assert.All(r.Ticks, t => Assert.Equal(2, t.PerCpu.Length));
        Assert.All(r.PerCpuAverage, v => Assert.True(v > 0));
        Assert.Null(r.Steadiness);   // 3 秒では判定しない
    }

    [Fact]
    public void Computation_error_is_the_heaviest_finding()
    {
        var r = new CpuLoadResult { ComputeErrors = 2, Chunks = 1000 };
        var f = CpuLoadTest.Judge(r);
        Assert.Equal(Verdict.Check, f[0].Verdict);
        Assert.Contains("間違えて", f[0].Reason);
    }

    [Fact]
    public void Slow_core_is_compared_only_with_its_own_kind()
    {
        // 2 種類のコア（P=1, E=0）。E コアが P コアの半分なのは正常
        var cpus = new List<LogicalCpu>
        {
            new(0, 0, 0, 0, 1), new(1, 0, 1, 1, 1), new(2, 0, 2, 2, 1),
            new(3, 0, 3, 3, 0), new(4, 0, 4, 4, 0), new(5, 0, 5, 5, 0),
        };
        var even = new CpuLoadResult { Cpus = cpus, PerCpuAverage = new double[] { 100, 101, 99, 50, 51, 49 }, Chunks = 1 };
        Assert.Equal(Verdict.Ok, CpuLoadTest.Judge(even).Single(f => f.Title == "コアごとの速さ").Verdict);

        var oneSlow = new CpuLoadResult { Cpus = cpus, PerCpuAverage = new double[] { 100, 40, 99, 50, 51, 49 }, Chunks = 1 };
        var f = CpuLoadTest.Judge(oneSlow).Single(f => f.Title == "コアごとの速さ");
        Assert.Equal(Verdict.Caution, f.Verdict);
        Assert.Contains("#1", f.Reason);
    }

    private sealed class SyncProgress<T>(Action<T> act) : IProgress<T>
    {
        public void Report(T value) => act(value);
    }
}

public class SteadinessTests
{
    [Fact]
    public void Flat_series_scores_100()
    {
        var s = Steadiness.Compute(Enumerable.Repeat(100.0, 30).ToList())!;
        Assert.Equal(100, s.Score, 3);
        Assert.Equal(0, s.Cv, 3);
        Assert.Equal(Verdict.Ok, Steadiness.Judge(s));
    }

    [Fact]
    public void Throttled_series_is_flagged()
    {
        // 序盤 100、終盤 60（熱で絞られた形）
        var series = Enumerable.Range(0, 30).Select(i => i < 12 ? 100.0 : 60.0).ToList();
        var s = Steadiness.Compute(series)!;
        Assert.Equal(60, s.Score, 0);
        Assert.Equal(Verdict.Check, Steadiness.Judge(s));
    }

    [Fact]
    public void Warm_up_samples_are_skipped_and_short_series_is_not_judged()
    {
        var series = new List<double> { 10, 20, 100, 100, 100, 100, 100, 100 };
        Assert.Equal(100, Steadiness.Compute(series)!.Score, 3);
        Assert.Null(Steadiness.Compute(new List<double> { 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Fact]
    public void Rising_series_is_capped_at_100()
    {
        var s = Steadiness.Compute(Enumerable.Range(0, 20).Select(i => 50.0 + i).ToList())!;
        Assert.Equal(100, s.Score, 3);
    }
}

public class CpuTopologyTests
{
    [Fact]
    public void Processor_core_records_are_expanded_to_logical_cpus()
    {
        // RelationProcessorCore を2件（各 48 バイト）。1件目は SMT で 2 論理（EfficiencyClass 1）、2件目は 1 論理（0）
        var buf = new byte[96];
        void Core(int off, byte eff, ulong mask)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(off), 0);       // Relationship
            BitConverter.TryWriteBytes(buf.AsSpan(off + 4), 48);  // Size
            buf[off + 9] = eff;
            BitConverter.TryWriteBytes(buf.AsSpan(off + 30), (ushort)1);
            BitConverter.TryWriteBytes(buf.AsSpan(off + 32), mask);
            BitConverter.TryWriteBytes(buf.AsSpan(off + 40), (ushort)0);
        }
        Core(0, 1, 0b0011);
        Core(48, 0, 0b0100);
        var cpus = CpuTopology.Parse(buf);
        Assert.Equal(3, cpus.Count);
        Assert.Equal(new byte[] { 1, 1, 0 }, cpus.Select(c => c.EfficiencyClass).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, cpus.Select(c => c.Core).ToArray());
        Assert.Equal(new byte[] { 0, 1, 2 }, cpus.Select(c => c.Number).ToArray());
    }

    [Fact]
    public void Real_machine_topology_matches_processor_count()
    {
        Assert.Equal(Environment.ProcessorCount, CpuTopology.Enumerate().Count);
    }
}
