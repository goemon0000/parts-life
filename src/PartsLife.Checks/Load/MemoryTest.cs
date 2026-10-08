using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PartsLife.Checks.Load;

public sealed class MemoryTestOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>空きメモリのうち何割を使うか。**0.5 を超える値は 0.5 に切り詰める。**</summary>
    public double FractionOfAvailable { get; init; } = 0.25;
    /// <summary>直接バイト数で指定する（テスト用）。指定しても上の上限と下限は効く。</summary>
    public long? Bytes { get; init; }
    public int? Threads { get; init; }
}

public sealed record MemoryTick(int Pass, PatternKind Pattern, bool Verifying, double MBps,
                                long Mismatches, long Bytes, double Seconds);

public sealed class MemoryTestResult
{
    public long BytesTested { get; set; }
    public long TotalPhys { get; set; }
    public long AvailableAtStart { get; set; }
    public int Passes { get; set; }
    public double WriteMBps { get; set; }
    public double ReadMBps { get; set; }
    public long Mismatches { get; set; }
    public List<(PatternKind Pattern, Mismatch Where)> FirstMismatches { get; } = new();
    public bool Stopped { get; set; }
    public string? AbortReason { get; set; }
    public double Seconds { get; set; }
}

/// <summary>
/// メモリの書き込み・照合テスト。**利用者の権限の範囲で**、確保できた領域に模様を書いて読み戻す。
///
/// ■ 量は控えめに
/// 既定は空きの 25%、最大でも 50%。さらに「残りの空きが 1GB か搭載量の 10% の大きい方」を
/// 割り込まないところで確保をやめる。**ページファイルに追い出し合いを始めたら、
/// メモリではなくディスクを試験していることになる**し、他のアプリが固まる。
/// 確保した 64MB ごとに全ページへ書いてから次の空きを確かめる（書くまでは物理メモリが割り当てられず、
/// 空きが減らないので、確かめても意味がない）。
/// ■ 試験中も空きを見張る
/// 他のアプリがメモリを欲しがったら、こちらが引く（中止して返す）。
/// ■ 正直に
/// これは OS が貸してくれた領域だけの試験で、メモリ全体の検査ではない
/// （全域を見るには Windows メモリ診断のように OS の外から調べる必要がある）。
/// 速さも「この試験での実測」であって、メモリの最大帯域ではない。
/// </summary>
public static class MemoryTest
{
    public const double MaxFraction = 0.5;
    public const long BlockBytes = 64L << 20;
    private const int SliceWords = (4 << 20) / 8;      // 4 MiB ごとに止めの旗を見る

    public static Task<MemoryTestResult> RunAsync(MemoryTestOptions o, IProgress<MemoryTick>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(o, progress, ct), CancellationToken.None,
                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>この機械で実際に使う量。**純粋な関数**（テストで上限・下限を確かめる）。</summary>
    public static long PlanBytes(long availPhys, long totalPhys, double fraction, long? explicitBytes)
    {
        fraction = Math.Clamp(fraction, 0, MaxFraction);
        long floor = Math.Max(1L << 30, totalPhys / 10);
        long want = explicitBytes ?? (long)(availPhys * fraction);
        want = Math.Min(want, (long)(availPhys * MaxFraction));
        want = Math.Min(want, availPhys - floor);
        return want <= 0 ? 0 : want - want % BlockBytes;
    }

    public static unsafe MemoryTestResult Run(MemoryTestOptions o, IProgress<MemoryTick>? progress, CancellationToken ct)
    {
        var r = new MemoryTestResult();
        var (total, avail) = MemoryStatus();
        r.TotalPhys = total;
        r.AvailableAtStart = avail;
        long floor = Math.Max(1L << 30, total / 10);
        long plan = PlanBytes(avail, total, o.FractionOfAvailable, o.Bytes);
        if (plan < BlockBytes)
        {
            r.AbortReason = L.T("空きメモリが少ないので行いませんでした", "not enough free memory to run the test");
            return r;
        }

        var blocks = new List<IntPtr>();
        var sw = Stopwatch.StartNew();
        try
        {
            // 少しずつ確保し、そのたびに空きを確かめる。確保しただけでは物理メモリは減らないので、
            // 全ページに1回書いて実際に割り当てさせてから次へ進む（でないと空きの確認が素通りになる）
            for (long got = 0; got < plan; got += BlockBytes)
            {
                if (ct.IsCancellationRequested) { r.Stopped = true; return r; }
                if (MemoryStatus().Avail - BlockBytes < floor) break;
                void* p = NativeMemory.AlignedAlloc((nuint)BlockBytes, 4096);
                if (p is null) break;
                blocks.Add((IntPtr)p);
                TouchPages((byte*)p, BlockBytes);
            }
            if (blocks.Count == 0)
            {
                r.AbortReason = L.T("メモリを確保できませんでした", "could not allocate memory");
                return r;
            }
            r.BytesTested = blocks.Count * BlockBytes;

            int threads = Math.Clamp(o.Threads ?? Environment.ProcessorCount / 2, 1, Environment.ProcessorCount);
            long wordsPerBlock = BlockBytes / 8;
            double writeBytes = 0, writeSec = 0, readBytes = 0, readSec = 0;
            var rng = new Random();
            var kinds = Enum.GetValues<PatternKind>();
            object gate = new();

            for (int pass = 0; ; pass++)
            {
                var kind = kinds[pass % kinds.Length];
                ulong seed = (ulong)rng.NextInt64();

                foreach (bool verify in new[] { false, true })
                {
                    if (ct.IsCancellationRequested) { r.Stopped = true; return r; }
                    if (sw.Elapsed >= o.Duration) return r;
                    if (MemoryStatus().Avail < floor / 2)
                    {
                        r.AbortReason = L.T("ほかのアプリがメモリを必要としたので、途中で引きました",
                                            "stopped early because other apps needed the memory");
                        return r;
                    }

                    var phase = Stopwatch.StartNew();
                    long doneBytes = 0;
                    var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
                    Parallel.For(0, blocks.Count, po, (b, state) =>
                    {
                        var span = new Span<ulong>((void*)blocks[b], (int)wordsPerBlock);
                        for (int s = 0; s < span.Length; s += SliceWords)
                        {
                            if (ct.IsCancellationRequested || sw.Elapsed >= o.Duration) { state.Stop(); return; }
                            var slice = span.Slice(s, Math.Min(SliceWords, span.Length - s));
                            long baseIndex = b * wordsPerBlock + s;
                            if (!verify) Patterns.Fill(slice, kind, seed, baseIndex);
                            else
                            {
                                var v = Patterns.Verify(slice, kind, seed, baseIndex);
                                if (v.Mismatches > 0)
                                    lock (gate)
                                    {
                                        r.Mismatches += v.Mismatches;
                                        foreach (var m in v.First)
                                            if (r.FirstMismatches.Count < 16) r.FirstMismatches.Add((kind, m));
                                    }
                            }
                            Interlocked.Add(ref doneBytes, slice.Length * 8L);
                        }
                    });
                    double sec = phase.Elapsed.TotalSeconds;
                    if (verify) { readBytes += doneBytes; readSec += sec; } else { writeBytes += doneBytes; writeSec += sec; }
                    r.WriteMBps = writeSec > 0 ? writeBytes / 1048576.0 / writeSec : 0;
                    r.ReadMBps = readSec > 0 ? readBytes / 1048576.0 / readSec : 0;
                    // 照合まで済んだ周回だけを「1周」と数える
                    if (verify && doneBytes == r.BytesTested) r.Passes = pass + 1;
                    progress?.Report(new MemoryTick(pass + 1, kind, verify,
                        sec > 0 ? doneBytes / 1048576.0 / sec : 0, r.Mismatches, r.BytesTested, sw.Elapsed.TotalSeconds));
                }
            }
        }
        finally
        {
            foreach (var p in blocks) NativeMemory.AlignedFree((void*)p);
            r.Seconds = sw.Elapsed.TotalSeconds;
        }
    }

    /// <summary>各ページの先頭に 0 を書き、OS に物理メモリを割り当てさせる。</summary>
    internal static unsafe void TouchPages(byte* p, long length)
    {
        for (long off = 0; off < length; off += 4096) Volatile.Write(ref p[off], (byte)0);
    }

    public static IReadOnlyList<Finding> Judge(MemoryTestResult r)
    {
        var f = new List<Finding>();
        if (r.BytesTested == 0)
        {
            f.Add(new Finding(L.T("メモリ", "Memory"), Verdict.Unavailable, r.AbortReason ?? L.T("行えませんでした", "did not run")));
            return f;
        }
        string size = $"{r.BytesTested / 1073741824.0:0.0} GB";
        if (r.Mismatches > 0)
        {
            var m = r.FirstMismatches.FirstOrDefault();
            string where = m.Where is null ? "" :
                L.T($"（例: {m.Pattern} で 0x{m.Where.Expected:X16} のはずが 0x{m.Where.Actual:X16}、{m.Where.FlippedBits} ビット違い）",
                    $" (e.g. {m.Pattern}: expected 0x{m.Where.Expected:X16}, read 0x{m.Where.Actual:X16}, {m.Where.FlippedBits} bit(s) differ)");
            f.Add(new Finding(L.T("書き込みと照合", "Write / verify"), Verdict.Check,
                L.T($"{L.N(r.Mismatches)} か所で、書いた値と読んだ値が違いました。メモリかその設定（XMP/EXPO・電圧）に問題がある可能性が高い深刻な所見です{where}",
                    $"{L.N(r.Mismatches)} location(s) read back different from what was written. This is a serious finding: the memory or its settings (XMP/EXPO, voltage) are likely at fault{where}")));
        }
        else if (r.Passes == 0)
            f.Add(new Finding(L.T("書き込みと照合", "Write / verify"), Verdict.Unavailable,
                L.T($"{size} を確保しましたが、1 周する前に終わりました", $"{size} allocated, but stopped before completing one pass")));
        else
            f.Add(new Finding(L.T("書き込みと照合", "Write / verify"), Verdict.Ok,
                L.T($"{size} を {r.Passes} 周、食い違いなし", $"{size}, {r.Passes} pass(es), no mismatches")));

        f.Add(new Finding(L.T("この試験での速さ", "Throughput in this test"), Verdict.Ok,
            L.T($"書き {r.WriteMBps / 1024:0.0} GB/s / 照合 {r.ReadMBps / 1024:0.0} GB/s（メモリの最大帯域ではありません）",
                $"write {r.WriteMBps / 1024:0.0} GB/s / verify {r.ReadMBps / 1024:0.0} GB/s (not the peak bandwidth)")));
        f.Add(new Finding(L.T("範囲", "Coverage"), Verdict.Ok,
            L.T($"Windows が使える {r.TotalPhys / 1073741824.0:0.0} GB のうち、貸してくれた {size} だけを試験しました。全域の検査は Windows メモリ診断で",
                $"tested only the {size} Windows lent us out of {r.TotalPhys / 1073741824.0:0.0} GB usable; use Windows Memory Diagnostic for a full test")));
        if (r.AbortReason is { } why)
            f.Add(new Finding(L.T("中断", "Stopped early"), Verdict.Caution, why));
        return f;
    }

    public static (long Total, long Avail) MemoryStatus()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return (0, 0);
        return ((long)st.ullTotalPhys, (long)st.ullAvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
