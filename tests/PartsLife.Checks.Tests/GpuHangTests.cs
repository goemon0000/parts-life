using System.Diagnostics;
using PartsLife.Checks.Gpu;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 温度の読み取りが戻ってこないとき。**負荷を止められなくなる / 読み取り中に DLL を解放する**ことが無いように。
/// GPU は使わない（WARP = CPU で、数秒だけ）。
/// </summary>
public class GpuHangTests
{
    [Fact]
    public void Timed_reader_gives_up_on_a_hung_read_and_does_not_pile_up()
    {
        using var gate = new ManualResetEventSlim(false);
        int calls = 0;
        using var reader = new TimedReader(() => { Interlocked.Increment(ref calls); gate.Wait(); return 42; }, "test");
        var sw = Stopwatch.StartNew();
        Assert.False(reader.TryRead(TimeSpan.FromMilliseconds(200), out _));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(reader.Busy);
        // 固まっている間は新しい読み取りを送り込まない
        sw.Restart();
        Assert.False(reader.TryRead(TimeSpan.FromSeconds(5), out _));
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.Equal(1, Volatile.Read(ref calls));

        gate.Set();
        SpinWait.SpinUntil(() => !reader.Busy, 2000);
        Assert.False(reader.Busy);
        Assert.True(reader.TryRead(TimeSpan.FromSeconds(2), out var v));
        Assert.Equal(42, v);
    }

    [Fact]
    public void Guard_refuses_to_unload_while_someone_is_inside()
    {
        var g = new InsideGuard();
        Assert.True(g.TryEnter());
        Assert.False(g.TryClose());          // 中に糸がいる → 解放してはいけない
        Assert.False(g.TryEnter());          // 閉じた後は誰も入れない
        g.Exit();

        var g2 = new InsideGuard();
        Assert.True(g2.TryEnter());
        g2.Exit();
        Assert.True(g2.TryClose());          // 誰もいない → 解放してよい
        Assert.False(g2.TryClose());         // 二度は解放しない
    }

    [Fact]
    public void Hung_reading_before_start_means_no_load_at_all()
    {
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var r = GpuLoadTest.Run(new GpuLoadOptions
            {
                AdapterIndex = null, Groups = 4, Duration = TimeSpan.FromSeconds(20),
                ReadTimeout = TimeSpan.FromMilliseconds(300),
                Temperature = () => { gate.Wait(); return 50; },
            }, null, CancellationToken.None);
            Assert.False(r.Ran);
            Assert.True(r.TemperatureReaderStuck);
            Assert.Contains("応答", r.AbortReason);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed.TotalSeconds:0.0} s");
        }
        finally { gate.Set(); }
    }

    [Fact]
    public void Reading_that_hangs_mid_run_aborts_the_load_promptly()
    {
        using var gate = new ManualResetEventSlim(false);
        int reads = 0;
        try
        {
            var r = GpuLoadTest.Run(new GpuLoadOptions
            {
                AdapterIndex = null, Groups = 4, Duration = TimeSpan.FromSeconds(20), TargetBatch = TimeSpan.FromMilliseconds(5),
                ReadTimeout = TimeSpan.FromMilliseconds(1500),
                // 最初の2回は読める。3回目から戻ってこない
                Temperature = () => { if (Interlocked.Increment(ref reads) >= 3) gate.Wait(); return 55; },
            }, null, CancellationToken.None);
            Assert.True(r.Ran);
            Assert.True(r.RanOnSoftware);                // 負荷は WARP（CPU）。本物の GPU には触らない
            Assert.Contains("応答", r.AbortReason);
            Assert.True(r.TemperatureReaderStuck);       // 呼んだ側は nvml.dll を解放してはいけない
            Assert.True(r.Seconds < 6, $"ran {r.Seconds:0.0} s");
            Assert.Equal(3, Volatile.Read(ref reads));   // 固まった読み取りに糸を送り込み続けない
        }
        finally { gate.Set(); }
    }
}
