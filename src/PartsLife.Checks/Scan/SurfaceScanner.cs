using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Scan;

public sealed class ScanIssue
{
    public long Offset { get; set; }
    public int Length { get; set; }
    /// <summary>読み取りの失敗なら Win32 のエラー番号。遅いだけなら 0。</summary>
    public int Error { get; set; }
    public double Ms { get; set; }
}

public sealed class ScanProgress
{
    public long DoneBytes { get; set; }
    public long PlannedBytes { get; set; }
    public long Errors { get; set; }
    public long Slow { get; set; }
    public double MBps { get; set; }
}

public sealed class ScanResult
{
    public int Disk { get; set; }
    public bool Full { get; set; }
    public int Every { get; set; }
    public int BlockBytes { get; set; }
    public long DiskBytes { get; set; }
    public long PlannedBytes { get; set; }
    public long ReadBytes { get; set; }
    public long Blocks { get; set; }
    public long ErrorCount { get; set; }
    public long SlowCount { get; set; }
    public List<ScanIssue> Errors { get; set; } = new();
    public List<ScanIssue> SlowBlocks { get; set; } = new();
    public double MedianMs { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    public double Seconds { get; set; }
    public bool Stopped { get; set; }
    /// <summary>途中で打ち切った理由（利用者が止めた以外）。</summary>
    public string? AbortReason { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double AverageMBps => Seconds <= 0 ? 0 : ReadBytes / 1048576.0 / Seconds;
}

/// <summary>
/// 読むだけの表面検査。**管理者で動く PartsLife.Check.exe からだけ呼ぶ。**
///
/// ■ 書かない
/// handle は GENERIC_READ だけで開く。書き込みの権利を持たない handle なので、
/// 間違って書く命令を投げても OS が断る。
/// ■ キャッシュを通さない（FILE_FLAG_NO_BUFFERING）
/// 通すと、2回目以降はメモリから返ってきて媒体を読まない。それでは検査にならない。
/// ■ 弱っている機器を痛めつけない
/// 読めない所が続いたら打ち切る。壊れかけの HDD を全域で何度も擦ると、それで止めを刺すことがある。
/// </summary>
public static class SurfaceScanner
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

    /// <summary>読めない所がこれだけ出たら打ち切る。</summary>
    public const int MaxErrors = 100;
    /// <summary>結果に載せる件数の上限（数は全部数える）。</summary>
    public const int MaxListed = 200;

    public static unsafe ScanResult Run(int disk, bool full, int every, CancellationToken ct,
                                        Action<ScanProgress>? progress = null, int blockBytes = ScanPlan.DefaultBlockBytes)
    {
        var result = new ScanResult { Disk = disk, Full = full, Every = full ? 1 : Math.Max(2, every), BlockBytes = blockBytes };
        using var h = StorageReader.CreateFileW($@"\\.\PhysicalDrive{disk}", GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (h.IsInvalid)
        {
            result.AbortReason = L.T($"ディスクを開けませんでした（{Marshal.GetLastPInvokeError()}）",
                                     $"could not open the disk (error {Marshal.GetLastPInvokeError()})");
            return result;
        }

        var geo = new byte[256];
        int got;
        fixed (byte* pg = geo)
        {
            if (!StorageReader.DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, pg, geo.Length, out got, IntPtr.Zero))
            {
                result.AbortReason = L.T("大きさを問い合わせられませんでした", "could not query the disk size");
                return result;
            }
        }
        var (size, sector) = StorageProtocol.ParseGeometryEx(geo.AsSpan(0, got));
        if (size <= 0 || sector <= 0 || blockBytes % sector != 0)
        {
            result.AbortReason = L.T("大きさが読めませんでした", "the disk reported no usable size");
            return result;
        }
        result.DiskBytes = size;
        result.PlannedBytes = ScanPlan.PlannedBytes(size, blockBytes, result.Every, sector);

        var tracker = new LatencyTracker();
        // FILE_FLAG_NO_BUFFERING はバッファの番地もセクタ境界に揃っている必要がある
        byte* buffer = (byte*)NativeMemory.AlignedAlloc((nuint)blockBytes, 4096);
        var total = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        long lastBytes = 0;
        try
        {
            foreach (var (offset, length) in ScanPlan.Blocks(size, blockBytes, result.Every, sector))
            {
                if (ct.IsCancellationRequested) { result.Stopped = true; break; }

                var t = Stopwatch.GetTimestamp();
                bool ok = ReadAt(h, buffer, offset, length, out int read, out int error);
                double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                result.Blocks++;

                if (!ok || read != length)
                {
                    result.ErrorCount++;
                    if (result.Errors.Count < MaxListed)
                        result.Errors.Add(new ScanIssue { Offset = offset, Length = length, Error = ok ? -1 : error, Ms = ms });
                    if (IsGone(error))
                    {
                        result.AbortReason = L.T("ディスクが応答しなくなりました", "the disk stopped responding");
                        break;
                    }
                    if (result.ErrorCount >= MaxErrors)
                    {
                        result.AbortReason = L.T($"読めない所が {MaxErrors} か所に達したので打ち切りました。先にバックアップを",
                                                 $"stopped after {MaxErrors} unreadable blocks — back up first");
                        break;
                    }
                    continue;
                }

                result.ReadBytes += read;
                if (tracker.Add(ms))
                {
                    result.SlowCount++;
                    if (result.SlowBlocks.Count < MaxListed)
                        result.SlowBlocks.Add(new ScanIssue { Offset = offset, Length = length, Ms = ms });
                }

                if (progress is not null && lastReport.ElapsedMilliseconds >= 250)
                {
                    double sec = lastReport.Elapsed.TotalSeconds;
                    progress(new ScanProgress
                    {
                        DoneBytes = result.ReadBytes, PlannedBytes = result.PlannedBytes,
                        Errors = result.ErrorCount, Slow = result.SlowCount,
                        MBps = (result.ReadBytes - lastBytes) / 1048576.0 / sec,
                    });
                    lastBytes = result.ReadBytes;
                    lastReport.Restart();
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        result.Seconds = total.Elapsed.TotalSeconds;
        result.MedianMs = tracker.Median;
        result.P99Ms = tracker.Percentile(0.99);
        result.MaxMs = tracker.MaxMs;
        return result;
    }

    /// <summary>機器ごと居なくなった類の誤り。続けても意味が無い。</summary>
    private static bool IsGone(int error) => error is 21 /*NOT_READY*/ or 433 /*NO_SUCH_DEVICE*/
                                                    or 1167 /*DEVICE_NOT_CONNECTED*/ or 55 /*DEV_NOT_EXIST*/;

    private static unsafe bool ReadAt(SafeFileHandle h, byte* buffer, long offset, int length, out int read, out int error)
    {
        var ov = new NativeOverlapped
        {
            OffsetLow = unchecked((int)(offset & 0xFFFFFFFF)),
            OffsetHigh = unchecked((int)(offset >> 32)),
        };
        bool ok = ReadFile(h, buffer, length, out read, &ov);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle h, byte* buffer, int toRead, out int read, NativeOverlapped* overlapped);
}
