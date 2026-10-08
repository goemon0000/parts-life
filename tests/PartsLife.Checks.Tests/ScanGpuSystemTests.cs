using PartsLife.Checks.Gpu;
using PartsLife.Checks.Scan;
using PartsLife.Checks.SystemInfo;

namespace PartsLife.Checks.Tests;

public class ScanPlanTests
{
    private const int MiB = 1 << 20;

    [Fact]
    public void Full_mode_covers_every_byte_once()
    {
        long total = 10L * MiB + 4096 * 3;     // 末尾に半端なブロック
        var blocks = ScanPlan.Blocks(total, MiB, 1, 512).ToList();
        Assert.Equal(11, blocks.Count);
        Assert.Equal(total, blocks.Sum(b => (long)b.Length));
        Assert.Equal(10L * MiB, blocks[^1].Offset);
        Assert.Equal(4096 * 3, blocks[^1].Length);
        Assert.Equal(total, ScanPlan.PlannedBytes(total, MiB, 1, 512));
    }

    [Fact]
    public void Sampling_reads_every_nth_block_and_always_the_last()
    {
        long total = 1000L * MiB;
        var blocks = ScanPlan.Blocks(total, MiB, 256, 4096).ToList();
        Assert.Equal(new long[] { 0, 256L * MiB, 512L * MiB, 768L * MiB, 999L * MiB }, blocks.Select(b => b.Offset).ToArray());
        Assert.Equal(5L * MiB, ScanPlan.PlannedBytes(total, MiB, 256, 4096));
    }

    [Fact]
    public void Lengths_are_whole_sectors()
    {
        long total = 3L * MiB + 4096 + 100;     // 4096 バイトセクタで、最後の 100 バイトは読めない
        var blocks = ScanPlan.Blocks(total, MiB, 1, 4096).ToList();
        Assert.All(blocks, b => Assert.Equal(0, b.Length % 4096));
        Assert.Equal(3L * MiB + 4096, blocks.Sum(b => (long)b.Length));
    }

    [Fact]
    public void Block_size_must_be_a_multiple_of_the_sector()
    {
        Assert.Throws<ArgumentException>(() => ScanPlan.Blocks(MiB, 1000, 1, 512).ToList());
    }
}

public class LatencyTrackerTests
{
    [Fact]
    public void Slow_block_is_flagged_relative_to_the_median()
    {
        var t = new LatencyTracker { WarmUp = 10, Factor = 10, FloorMs = 50 };
        for (int i = 0; i < 100; i++) Assert.False(t.Add(8 + (i % 3)));     // HDD らしい 8〜10ms
        Assert.InRange(t.Median, 8, 11.5);
        Assert.True(t.Add(400));          // 40 倍
        Assert.False(t.Add(60));          // 床は越えたが中央値の 10 倍未満
        Assert.Equal(400, t.MaxMs);
    }

    [Fact]
    public void Fast_ssd_hiccups_below_the_floor_are_not_slow()
    {
        var t = new LatencyTracker { WarmUp = 10, Factor = 10, FloorMs = 50 };
        for (int i = 0; i < 100; i++) t.Add(0.5);
        Assert.False(t.Add(20));          // 中央値の 40 倍でも、20ms は遅いとは言わない
    }

    [Fact]
    public void Nothing_is_slow_during_warm_up()
    {
        var t = new LatencyTracker { WarmUp = 32 };
        Assert.False(t.Add(1));
        Assert.False(t.Add(10_000));
    }
}

public class GpuTests
{
    [Fact]
    public void Shader_compiles_on_the_cpu()
    {
        // d3dcompiler_47.dll は Windows 10 以降に同梱。GPU には触らない
        Assert.True(D3D11Compute.CompileForTest() > 100);
    }

    [Fact]
    public void Warp_device_matches_the_cpu_reference()
    {
        // WARP は CPU で動く D3D11。**GPU は一切使わない。** vtable の番号と答え合わせの検証用
        using var gpu = new D3D11Compute(adapterIndex: null, groups: 2);
        // 作られたデバイスから確かめる（番号の指定を信じるだけにしない）
        Assert.True(gpu.IsSoftware, gpu.DeviceAdapterName);
        gpu.Dispatch(seed: 777, iters: 50, TimeSpan.FromSeconds(10));
        var got = gpu.ReadBack(512);
        Assert.Equal(512, got.Length);
        for (uint i = 0; i < got.Length; i++) Assert.Equal(D3D11Compute.Reference(i, 777, 50), got[i]);
    }

    [Fact]
    public void Gpu_without_a_temperature_source_is_never_loaded()
    {
        var r = GpuLoadTest.Run(new GpuLoadOptions { AdapterIndex = null, Temperature = null }, null, CancellationToken.None);
        Assert.False(r.Ran);
        Assert.Contains("温度", r.AbortReason);
        Assert.Equal(Verdict.Unavailable, GpuLoadTest.Judge(r)[0].Verdict);
    }

    [Fact]
    public void Already_hot_gpu_is_not_started()
    {
        var r = GpuLoadTest.Run(new GpuLoadOptions { AdapterIndex = null, Temperature = () => 79 }, null, CancellationToken.None);
        Assert.False(r.Ran);
        Assert.True(r.AbortedForHeat);
    }

    [Fact]
    public void Reaching_83_degrees_aborts_the_run()
    {
        // WARP（CPU）に、温度だけ作り物を渡す。2 回目以降の読み取りで 83 ℃
        int reads = 0;
        var r = GpuLoadTest.Run(new GpuLoadOptions
        {
            AdapterIndex = null, Groups = 4, Duration = TimeSpan.FromSeconds(20),
            TargetBatch = TimeSpan.FromMilliseconds(5),
            Temperature = () => Interlocked.Increment(ref reads) >= 2 ? 83 : 60,
        }, null, CancellationToken.None);
        Assert.True(r.Ran);
        Assert.True(r.RanOnSoftware);
        Assert.True(r.AbortedForHeat);
        Assert.True(r.Seconds < 5, $"ran {r.Seconds:0.0} s");
        Assert.Equal(83, r.MaxTemperature);
        Assert.Equal(Verdict.Check, GpuLoadTest.Judge(r).Single(f => f.Title == "中断").Verdict);
    }

    [Fact]
    public void Lost_temperature_readings_abort_the_run()
    {
        int reads = 0;
        var r = GpuLoadTest.Run(new GpuLoadOptions
        {
            AdapterIndex = null, Groups = 4, Duration = TimeSpan.FromSeconds(20), TargetBatch = TimeSpan.FromMilliseconds(5),
            Temperature = () => Interlocked.Increment(ref reads) == 1 ? 50 : null,
        }, null, CancellationToken.None);
        Assert.True(r.RanOnSoftware);
        Assert.True(r.Seconds < 5);
        Assert.Contains("読めなく", r.AbortReason);
    }

    [Fact]
    public async Task Gpu_stop_is_prompt_on_warp()
    {
        using var cts = new CancellationTokenSource();
        var task = GpuLoadTest.RunAsync(new GpuLoadOptions
        {
            AdapterIndex = null, Groups = 4, Duration = TimeSpan.FromMinutes(5), TargetBatch = TimeSpan.FromMilliseconds(5),
            Temperature = () => 50,
        }, null, cts.Token);
        await Task.Delay(1500);
        cts.Cancel();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(r.RanOnSoftware);
        Assert.True(r.Stopped);
        Assert.Equal(0, r.Mismatches);
    }

    [Fact]
    public void Adapter_description_is_parsed_at_documented_offsets()
    {
        var d = new byte[312];
        var name = "NVIDIA GeForce RTX 3080 Ti";
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(d, 0);
        BitConverter.TryWriteBytes(d.AsSpan(256), 0x10DEu);
        BitConverter.TryWriteBytes(d.AsSpan(272), 12UL << 30);
        BitConverter.TryWriteBytes(d.AsSpan(296), 0xABCDu);
        BitConverter.TryWriteBytes(d.AsSpan(300), 0);
        BitConverter.TryWriteBytes(d.AsSpan(304), 0u);
        var p = GpuAdapters.ParseDesc1(d);
        Assert.Equal(name, p.Name);
        Assert.Equal(0x10DEu, p.VendorId);
        Assert.Equal(12UL << 30, p.Dedicated);
        Assert.Equal(0xABCDu, p.LuidLow);
    }
}

public class SystemJudgeTests
{
    [Fact]
    public void Module_below_rated_speed_is_information_not_an_error()
    {
        var f = SystemCheck.JudgeModule(new MemoryModule("DIMM 1", 16UL << 30, 3200, 2133, "", ""));
        Assert.Equal(Verdict.Caution, f.Verdict);
        Assert.Contains("情報", f.Reason);
        Assert.Contains("故障ではありません", f.Reason);
    }

    [Fact]
    public void Module_above_standard_speed_is_ok_and_explained()
    {
        // 実機（DDR5 4800 を EXPO で 5600）と同じ形
        var f = SystemCheck.JudgeModule(new MemoryModule("DIMM 1", 32UL << 30, 4800, 5600, "", ""));
        Assert.Equal(Verdict.Ok, f.Verdict);
        Assert.Contains("5600", f.Reason);
    }

    [Fact]
    public void Mixed_capacities_are_noted()
    {
        var f = SystemCheck.JudgeMemory(new[]
        {
            new MemoryModule("A", 16UL << 30, 3200, 3200, "", ""),
            new MemoryModule("B", 8UL << 30, 3200, 3200, "", ""),
        }).First();
        Assert.Equal(Verdict.Caution, f.Verdict);
    }

    [Fact]
    public void Whea_errors_are_a_check_and_unreadable_log_is_not_zero()
    {
        Assert.Equal(Verdict.Check, SystemCheck.JudgeEvents(new EventSource("whea", "WHEA-Logger", 2, 0)).Verdict);
        Assert.Equal(Verdict.Caution, SystemCheck.JudgeEvents(new EventSource("whea", "WHEA-Logger", 0, 5)).Verdict);
        Assert.Equal(Verdict.Ok, SystemCheck.JudgeEvents(new EventSource("whea", "WHEA-Logger", 0, 0)).Verdict);
        Assert.Equal(Verdict.Unavailable, SystemCheck.JudgeEvents(new EventSource("disk", "disk", null, null)).Verdict);
        Assert.Equal(Verdict.Caution, SystemCheck.JudgeEvents(new EventSource("kp41", "Kernel-Power 41", 3, null)).Verdict);
        Assert.Equal(Verdict.Check, SystemCheck.JudgeEvents(new EventSource("disk", "disk", 12, 0)).Verdict);
    }

    [Fact]
    public void Low_free_space_and_unhealthy_disk_are_flagged()
    {
        var d = new PhysicalDiskInfo(0, "Disk", "NVMe", 2_000_000_000_000, "SSD", 0, new ushort[] { 2 },
            new[] { new VolumeInfo("C:", 1_000_000_000_000, 50_000_000_000), new VolumeInfo("D:", 1_000_000_000_000, 500_000_000_000) });
        var f = SystemCheck.JudgeDisk(d).ToList();
        Assert.Equal(Verdict.Ok, f[0].Verdict);
        Assert.Equal(Verdict.Caution, f[1].Verdict);   // C: 5%
        Assert.Equal(Verdict.Ok, f[2].Verdict);

        var sick = d with { Health = 2, OperationalStatus = new ushort[] { 5 } };
        Assert.Equal(Verdict.Check, SystemCheck.JudgeDisk(sick).First().Verdict);
        var unknown = d with { Health = null, OperationalStatus = Array.Empty<ushort>() };
        Assert.Equal(Verdict.Unavailable, SystemCheck.JudgeDisk(unknown).First().Verdict);
    }

    [Fact]
    public void No_pagefile_is_a_caution()
    {
        Assert.Equal(Verdict.Caution, SystemCheck.JudgePagefile(new PagefileInfo(false, Array.Empty<PagefileEntry>())).Verdict);
        Assert.Equal(Verdict.Ok, SystemCheck.JudgePagefile(new PagefileInfo(true, new[] { new PagefileEntry(@"C:\pagefile.sys", 8192, 100, 2000) })).Verdict);
        Assert.Equal(Verdict.Caution, SystemCheck.JudgePagefile(new PagefileInfo(true, new[] { new PagefileEntry(@"C:\pagefile.sys", 1000, 100, 900) })).Verdict);
    }

    [Fact]
    public void Old_or_generic_gpu_driver_is_noted()
    {
        var now = new DateTime(2026, 10, 9);
        Assert.Equal(Verdict.Ok, SystemCheck.JudgeVideo(new VideoInfo("RTX", "32.0.15.6100", now.AddDays(-30), 0), now).Verdict);
        Assert.Equal(Verdict.Caution, SystemCheck.JudgeVideo(new VideoInfo("RTX", "1", now.AddYears(-3), 0), now).Verdict);
        Assert.Equal(Verdict.Check, SystemCheck.JudgeVideo(new VideoInfo("RTX", "1", now, 43), now).Verdict);
        Assert.Equal(Verdict.Caution, SystemCheck.JudgeVideo(new VideoInfo("Microsoft Basic Display Adapter", "10", now, 0), now).Verdict);
    }

    [Fact]
    public void Event_query_is_well_formed()
    {
        var q = EventCounts.BuildQuery(new[] { "disk", "Ntfs" }, new[] { 1, 2 }, EventCounts.Week, null);
        Assert.Equal("*[System[Provider[@Name='disk' or @Name='Ntfs'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 604800000]]]", q);
        Assert.Contains("EventID=41", EventCounts.BuildQuery(new[] { "x" }, new[] { 1 }, 1, 41));
    }
}
