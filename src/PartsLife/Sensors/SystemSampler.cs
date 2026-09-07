using System.Runtime.InteropServices;

namespace PartsLife.Sensors;

/// <summary>ある瞬間の機械の様子。表示側はこれだけを見る。</summary>
public sealed class Snapshot
{
    /// <summary>CPU全体の使用率 0..1</summary>
    public double CpuTotal { get; init; }
    /// <summary>論理プロセッサごとの使用率 0..1</summary>
    public IReadOnlyList<double> CpuCores { get; init; } = Array.Empty<double>();
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public double MemoryRatio => MemoryTotalBytes == 0 ? 0 : (double)MemoryUsedBytes / MemoryTotalBytes;
}

/// <summary>
/// 使用率を測る。
///
/// ■ 方針: **管理者権限が要るものは使わない。**
/// 温度・ファン・電圧はカーネルドライバが要るので、この版では扱わない。
/// ドライバを積むと ①UAC ②ウイルス対策ソフトの誤検知 ③配布の信用問題
/// が同時に乗ってくる。お遊びの常駐アプリが払うコストではない。
///
/// ■ 取り方
/// 全体は GetSystemTimes、コア別は NtQuerySystemInformation。
/// どちらも一般ユーザーで呼べる。PerformanceCounter は初期化が遅く、
/// 環境によっては壊れている（カウンタ再構築が要る）ので使わない。
/// </summary>
public sealed class SystemSampler
{
    // --- 全体 ---
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;

    // --- コア別 ---
    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? _prevPerCore;

    public int LogicalProcessorCount { get; } = Environment.ProcessorCount;

    public Snapshot Sample()
    {
        return new Snapshot
        {
            CpuTotal = SampleCpuTotal(),
            CpuCores = SampleCpuPerCore(),
            MemoryUsedBytes = MemoryUsed(out var total),
            MemoryTotalBytes = total,
        };
    }

    // -----------------------------------------------------------------------
    // CPU 全体
    // -----------------------------------------------------------------------
    private double SampleCpuTotal()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        long i = ToLong(idle), k = ToLong(kernel), u = ToLong(user);
        if (!_hasPrev)
        {
            (_prevIdle, _prevKernel, _prevUser, _hasPrev) = (i, k, u, true);
            return 0;
        }

        long di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
        (_prevIdle, _prevKernel, _prevUser) = (i, k, u);

        // kernel には idle が含まれる。総時間は kernel + user。
        long totalTicks = dk + du;
        if (totalTicks <= 0) return 0;
        double busy = (double)(totalTicks - di) / totalTicks;
        return Math.Clamp(busy, 0, 1);
    }

    // -----------------------------------------------------------------------
    // CPU コア別
    // -----------------------------------------------------------------------
    private IReadOnlyList<double> SampleCpuPerCore()
    {
        int n = LogicalProcessorCount;
        int size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size * n);
        try
        {
            int status = NtQuerySystemInformation(
                SystemProcessorPerformanceInformation, buffer, size * n, out _);
            if (status != 0) return Array.Empty<double>();

            var now = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[n];
            for (int i = 0; i < n; i++)
                now[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * size);

            if (_prevPerCore is null || _prevPerCore.Length != n)
            {
                _prevPerCore = now;
                return new double[n];
            }

            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                long idle   = now[i].IdleTime   - _prevPerCore[i].IdleTime;
                long kernel = now[i].KernelTime - _prevPerCore[i].KernelTime;
                long user   = now[i].UserTime   - _prevPerCore[i].UserTime;
                long total  = kernel + user;                 // kernel は idle を含む
                result[i] = total <= 0 ? 0 : Math.Clamp((double)(total - idle) / total, 0, 1);
            }
            _prevPerCore = now;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // -----------------------------------------------------------------------
    // メモリ
    // -----------------------------------------------------------------------
    private static ulong MemoryUsed(out ulong total)
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) { total = 0; return 0; }
        total = st.ullTotalPhys;
        return st.ullTotalPhys - st.ullAvailPhys;
    }

    // -----------------------------------------------------------------------
    // P/Invoke
    // -----------------------------------------------------------------------
    private static long ToLong(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>NtQuerySystemInformation の 8 番。論理プロセッサごとの時間が返る。</summary>
    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}
