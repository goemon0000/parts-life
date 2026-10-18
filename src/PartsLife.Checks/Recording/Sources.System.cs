using System.Globalization;
using System.Runtime.InteropServices;

namespace PartsLife.Checks.Recording;

/// <summary>
/// CPU の使用率（全体と論理プロセッサごと）・実効クロック・OS が報告する性能の制限・パッケージ電力。
/// **PDH だけ。管理者権限もドライバも要らない。** 温度・電圧は読まない（<see cref="ExtensionSource"/> が「追加機能が要る」と答える）。
///
/// - 実効クロック = 基準クロック（Processor Frequency）× % Processor Performance / 100。タスクマネージャーの「速度」と同じ出し方。
///   ブーストを含む。**特定のコアを測った周波数ではなく、OS が性能の比から出した値。**
/// - パッケージ電力は `\Energy Meter(*)\Power` の `*_PKG`（mW）。Windows が RAPL 相当の電力メーターを出している機械だけ読める。
///   値は OS・ファームウェアの報告で、推定を含むことがある（<see cref="Reasons.OsReported"/>）。
///   単位は、同じカウンタ集合の Energy（pWh）と Time（ms）の差分から出した電力と突き合わせて mW だと確かめてある（2026-10-18）。
///   それでも 0.5〜1500 W に収まらない値は、単位の取り違えを疑って使わない。
/// </summary>
internal sealed class CpuPdhSource : ISensorSource
{
    private const int MaxLogicalProcessors = 128;

    private PdhQuery? _q;
    private IntPtr _load, _perf, _freq, _limit, _flags, _energy;
    private string[] _lp = Array.Empty<string>();      // 論理プロセッサの PDH インスタンス名（"0,5"）を (group, index) 順に
    private bool _hasClock, _hasLimit, _hasFlags, _hasPower;
    private bool _disposed;

    public string Id => "cpu";

    public IReadOnlyList<MetricStatus> Detect()
    {
        var list = new List<MetricStatus>();
        _q = PdhQuery.TryOpen();
        if (_q is null)
        {
            foreach (var k in new[] { MetricKeys.CpuLoad, MetricKeys.CpuClock, MetricKeys.CpuPerfLimit, MetricKeys.CpuPerfLimitFlags, MetricKeys.CpuPackagePower })
                list.Add(new MetricStatus(k, MetricKeys.UnitOf(k), "pdh", Availability.Unavailable, Reasons.NoCounter));
            return list;
        }

        _load = _q.Add(@"\Processor Information(*)\% Processor Time");
        _perf = _q.Add(@"\Processor Information(*)\% Processor Performance");
        _freq = _q.Add(@"\Processor Information(*)\Processor Frequency");
        _limit = _q.Add(@"\Processor Information(*)\% Performance Limit");
        _flags = _q.Add(@"\Processor Information(*)\Performance Limit Flags");
        _energy = _q.Add(@"\Energy Meter(*)\Power");

        // PDH は 2 回集めて初めて差分の値が出る。まれに最初の差分が無効のまま返ることがあるので、
        // 全体の値が揃うまで（最大 3 回）間をおいて取り直す。カウンタそのものが無いときは待たない
        List<(string Instance, double Value)> load, perf, freq;
        for (int attempt = 0; ; attempt++)
        {
            _q.Collect();
            Thread.Sleep(300);
            _q.Collect();
            load = _q.ReadArray(_load, includeInvalid: true);
            perf = _q.ReadArray(_perf, noCap100: true, includeInvalid: true);
            freq = _q.ReadArray(_freq, includeInvalid: true);
            bool complete = HasTotal(load) && HasTotal(perf) && HasTotal(freq);
            bool hopeless = _load == IntPtr.Zero || _perf == IntPtr.Zero || _freq == IntPtr.Zero;
            if (complete || hopeless || attempt >= 2) break;
        }
        var limit = _q.ReadArray(_limit);
        var flags = _q.ReadArray(_flags);
        var energy = _q.ReadArray(_energy);

        _lp = LogicalProcessors(load);
        bool hasTotal = load.Any(x => IsTotal(x.Instance) && !double.IsNaN(x.Value));
        list.Add(hasTotal
            ? new MetricStatus(MetricKeys.CpuLoad, "%", "pdh:Processor Information", Availability.Available)
            : new MetricStatus(MetricKeys.CpuLoad, "%", "pdh:Processor Information", Availability.Unavailable, _load == IntPtr.Zero ? Reasons.NoCounter : Reasons.NoValue));

        _hasClock = perf.Any(x => IsTotal(x.Instance) && !double.IsNaN(x.Value)) && freq.Any(x => IsTotal(x.Instance) && !double.IsNaN(x.Value));
        list.Add(new MetricStatus(MetricKeys.CpuClock, "MHz", "pdh:Processor Information",
            _hasClock ? Availability.Available : Availability.Unavailable,
            _hasClock ? "" : (_perf == IntPtr.Zero || _freq == IntPtr.Zero ? Reasons.NoCounter : Reasons.NoValue)));

        _hasLimit = limit.Count > 0;
        list.Add(new MetricStatus(MetricKeys.CpuPerfLimit, "%", "pdh:Processor Information",
            _hasLimit ? Availability.Available : Availability.Unavailable,
            _hasLimit ? "" : (_limit == IntPtr.Zero ? Reasons.NoCounter : Reasons.NoValue)));

        _hasFlags = flags.Count > 0;
        list.Add(new MetricStatus(MetricKeys.CpuPerfLimitFlags, "bitmask", "pdh:Processor Information",
            _hasFlags ? Availability.Available : Availability.Unavailable,
            _hasFlags ? "" : (_flags == IntPtr.Zero ? Reasons.NoCounter : Reasons.NoValue)));

        double? watts = PackageWatts(energy);
        if (_energy == IntPtr.Zero)
            list.Add(new MetricStatus(MetricKeys.CpuPackagePower, "W", "pdh:Energy Meter", Availability.Unavailable, Reasons.NoCounter));
        else if (watts is null)
            list.Add(new MetricStatus(MetricKeys.CpuPackagePower, "W", "pdh:Energy Meter", Availability.Unavailable, Reasons.NoValue));
        else if (!PlausibleWatts(watts.Value))
            list.Add(new MetricStatus(MetricKeys.CpuPackagePower, "W", "pdh:Energy Meter", Availability.Unavailable, Reasons.Implausible));
        else
        {
            _hasPower = true;
            list.Add(new MetricStatus(MetricKeys.CpuPackagePower, "W", "pdh:Energy Meter", Availability.Available, Reasons.OsReported));
        }

        var perfNames = perf.Select(x => x.Instance).ToHashSet(StringComparer.Ordinal);
        var freqNames = freq.Select(x => x.Instance).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < _lp.Length; i++)
        {
            list.Add(new MetricStatus(MetricKeys.Lp(i, "load_pct"), "%", "pdh:Processor Information", Availability.Available));
            if (perfNames.Contains(_lp[i]) && freqNames.Contains(_lp[i]))
                list.Add(new MetricStatus(MetricKeys.Lp(i, "clock_mhz"), "MHz", "pdh:Processor Information", Availability.Available));
        }
        return list;
    }

    public void Prime() => _q?.Collect();

    public void Read(SampleRow row)
    {
        if (_q is null || !_q.Collect()) return;

        var load = _q.ReadArray(_load);
        foreach (var (name, v) in load)
            if (IsTotal(name)) row.Set(MetricKeys.CpuLoad, Math.Clamp(v, 0, 100));

        Dictionary<string, double>? perf = null, freq = null;
        if (_freq != IntPtr.Zero && _perf != IntPtr.Zero)
        {
            perf = ToMap(_q.ReadArray(_perf, noCap100: true));
            freq = ToMap(_q.ReadArray(_freq));
            if (_hasClock && perf.TryGetValue("_Total", out double p) && freq.TryGetValue("_Total", out double f))
                row.Set(MetricKeys.CpuClock, f * p / 100.0);
        }

        var loadMap = ToMap(load);
        for (int i = 0; i < _lp.Length; i++)
        {
            if (loadMap.TryGetValue(_lp[i], out double l)) row.Set(MetricKeys.Lp(i, "load_pct"), Math.Clamp(l, 0, 100));
            if (perf is not null && freq is not null && perf.TryGetValue(_lp[i], out double pp) && freq.TryGetValue(_lp[i], out double ff))
                row.Set(MetricKeys.Lp(i, "clock_mhz"), ff * pp / 100.0);
        }

        if (_hasLimit)
        {
            // 「どれかの論理プロセッサが制限されている」を見落とさないよう、最も低い値を取る
            double? min = null;
            foreach (var (name, v) in _q.ReadArray(_limit))
                if (!IsTotal(name) && !name.EndsWith(",_Total", StringComparison.Ordinal)) min = min is null ? v : Math.Min(min.Value, v);
            if (min is { } m) row.Set(MetricKeys.CpuPerfLimit, Math.Clamp(m, 0, 100));
        }

        if (_hasFlags)
        {
            // 論理プロセッサごとの印の和集合（平均ではなく「どれかに立っている bit」）
            ulong any = 0; bool seen = false;
            foreach (var (name, v) in _q.ReadArray(_flags))
            {
                if (IsTotal(name) || name.EndsWith(",_Total", StringComparison.Ordinal) || v < 0 || v > uint.MaxValue) continue;
                any |= (uint)v; seen = true;
            }
            if (seen) row.Set(MetricKeys.CpuPerfLimitFlags, any);
        }

        if (_hasPower && PackageWatts(_q.ReadArray(_energy)) is { } w && PlausibleWatts(w))
            row.Set(MetricKeys.CpuPackagePower, w);
    }

    private static bool HasTotal(List<(string Instance, double Value)> list) => list.Any(x => IsTotal(x.Instance) && !double.IsNaN(x.Value));

    internal static bool IsTotal(string instance) => string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, double> ToMap(List<(string Instance, double Value)> list)
    {
        var d = new Dictionary<string, double>(list.Count, StringComparer.Ordinal);
        foreach (var (n, v) in list) d[n] = v;
        return d;
    }

    /// <summary>`0,5` の形のインスタンス名だけを (グループ, 番号) 順に。`_Total` と `0,_Total` は論理プロセッサではない。</summary>
    internal static string[] LogicalProcessors(IEnumerable<(string Instance, double Value)> load)
    {
        var found = new List<(int Group, int Index, string Name)>();
        foreach (var (name, _) in load)
        {
            int comma = name.IndexOf(',');
            if (comma <= 0) continue;
            if (!int.TryParse(name.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out int g)) continue;
            if (!int.TryParse(name.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) continue;
            found.Add((g, n, name));
        }
        return found.OrderBy(x => x.Group).ThenBy(x => x.Index).Take(MaxLogicalProcessors).Select(x => x.Name).ToArray();
    }

    /// <summary>`RAPL_PackageN_PKG` の電力（mW）を足して W に。無ければ null。</summary>
    internal static double? PackageWatts(IEnumerable<(string Instance, double Value)> energy)
    {
        double sumMilliwatts = 0; bool any = false;
        foreach (var (name, v) in energy)
        {
            if (!name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) continue;
            sumMilliwatts += v; any = true;
        }
        return any ? sumMilliwatts / 1000.0 : null;
    }

    internal static bool PlausibleWatts(double w) => w >= 0.5 && w <= 1500;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _q?.Dispose();
    }
}

/// <summary>メモリの使用量。GlobalMemoryStatusEx（権限不要）。</summary>
internal sealed class MemorySource : ISensorSource
{
    public string Id => "memory";

    public IReadOnlyList<MetricStatus> Detect()
    {
        bool ok = TryRead(out _, out _);
        var state = ok ? Availability.Available : Availability.Unavailable;
        string why = ok ? "" : Reasons.Failed;
        return new[]
        {
            new MetricStatus(MetricKeys.MemUsedPct, "%", "GlobalMemoryStatusEx", state, why),
            new MetricStatus(MetricKeys.MemUsedMb, "MB", "GlobalMemoryStatusEx", state, why),
        };
    }

    public void Prime() { }

    public void Read(SampleRow row)
    {
        if (!TryRead(out ulong total, out ulong avail) || total == 0) return;
        row.Set(MetricKeys.MemUsedPct, (total - avail) * 100.0 / total);
        row.Set(MetricKeys.MemUsedMb, (total - avail) / 1048576.0);
    }

    internal static bool TryRead(out ulong total, out ulong avail)
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) { total = avail = 0; return false; }
        total = st.ullTotalPhys;
        avail = st.ullAvailPhys;
        return true;
    }

    /// <summary>搭載メモリの総量（MB）。記録の先頭に書く。</summary>
    internal static long TotalMb() => TryRead(out ulong t, out _) ? (long)(t / 1048576) : 0;

    public void Dispose() { }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

/// <summary>ディスクの活動（全体）。PDH の PhysicalDisk(_Total)。台ごとには分けない（この版は流量と忙しさだけ）。</summary>
internal sealed class DiskPdhSource : ISensorSource
{
    private PdhQuery? _q;
    private IntPtr _read, _write, _idle;
    private bool _hasRw, _hasIdle;

    public string Id => "disk";

    public IReadOnlyList<MetricStatus> Detect()
    {
        _q = PdhQuery.TryOpen();
        if (_q is not null)
        {
            _read = _q.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _write = _q.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _idle = _q.Add(@"\PhysicalDisk(_Total)\% Idle Time");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                _q.Collect();
                Thread.Sleep(250);
                _q.Collect();
                _hasRw = _q.Read(_read) is not null && _q.Read(_write) is not null;
                _hasIdle = _q.Read(_idle) is not null;
                if ((_hasRw && _hasIdle) || _read == IntPtr.Zero) break;   // まれに最初の差分が無効なので、1 回だけ取り直す
            }
        }
        const string src = "pdh:PhysicalDisk";
        return new[]
        {
            Status(MetricKeys.DiskRead, "MB/s", src, _hasRw, _read),
            Status(MetricKeys.DiskWrite, "MB/s", src, _hasRw, _write),
            Status(MetricKeys.DiskBusy, "%", src, _hasIdle, _idle),
        };
    }

    private static MetricStatus Status(string key, string unit, string src, bool ok, IntPtr counter) =>
        new(key, unit, src, ok ? Availability.Available : Availability.Unavailable,
            ok ? "" : (counter == IntPtr.Zero ? Reasons.NoCounter : Reasons.NoValue));

    public void Prime() => _q?.Collect();

    public void Read(SampleRow row)
    {
        if (_q is null || !_q.Collect()) return;
        if (_hasRw)
        {
            if (_q.Read(_read) is { } r) row.Set(MetricKeys.DiskRead, Math.Max(0, r) / 1048576.0);
            if (_q.Read(_write) is { } w) row.Set(MetricKeys.DiskWrite, Math.Max(0, w) / 1048576.0);
        }
        if (_hasIdle && _q.Read(_idle) is { } idle) row.Set(MetricKeys.DiskBusy, Math.Clamp(100 - idle, 0, 100));
    }

    public void Dispose() => _q?.Dispose();
}

/// <summary>電源の状態（AC か電池か、電池の残り）。GetSystemPowerStatus（権限不要）。</summary>
internal sealed class PowerStateSource : ISensorSource
{
    private bool _hasAc, _hasBattery;

    public string Id => "power-state";

    public IReadOnlyList<MetricStatus> Detect()
    {
        bool ok = TryRead(out var st);
        _hasAc = ok && st.ACLineStatus is 0 or 1;
        _hasBattery = ok && st.BatteryFlag != 128 && st.BatteryFlag != 255 && st.BatteryLifePercent <= 100;
        return new[]
        {
            new MetricStatus(MetricKeys.PowerAc, "0/1", "GetSystemPowerStatus",
                _hasAc ? Availability.Available : Availability.Unavailable, _hasAc ? "" : (ok ? Reasons.AcUnknown : Reasons.Failed)),
            new MetricStatus(MetricKeys.PowerBattery, "%", "GetSystemPowerStatus",
                _hasBattery ? Availability.Available : Availability.Unavailable, _hasBattery ? "" : (ok ? Reasons.NoBattery : Reasons.Failed)),
        };
    }

    public void Prime() { }

    public void Read(SampleRow row)
    {
        if (!TryRead(out var st)) return;
        if (_hasAc && st.ACLineStatus is 0 or 1) row.Set(MetricKeys.PowerAc, st.ACLineStatus);
        if (_hasBattery && st.BatteryLifePercent <= 100) row.Set(MetricKeys.PowerBattery, st.BatteryLifePercent);
    }

    private static bool TryRead(out SYSTEM_POWER_STATUS st) => GetSystemPowerStatus(out st);

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>
/// 標準の読み方では読めない項目を、「追加機能が要る」と**答えるだけ**の読み口。値は読まない。
/// CPU の温度・コア電圧、マザーボードのファン・温度は、カーネルドライバ（段 3 で LibreHardwareMonitor ＋ PawnIO の別プロセス）が要る。
/// 画面に見せることで、足りない物を「測れていない」と正直に言う（OK と出さない）ための行。
/// </summary>
internal sealed class ExtensionSource : ISensorSource
{
    public string Id => "extension";

    public IReadOnlyList<MetricStatus> Detect() => new[]
    {
        Need(MetricKeys.CpuTemp), Need(MetricKeys.CpuVoltage), Need(MetricKeys.BoardFan), Need(MetricKeys.BoardTemp),
    };

    private static MetricStatus Need(string key) =>
        new(key, MetricKeys.UnitOf(key), "extension", Availability.NeedsExtension, Reasons.NeedsDriver);

    public void Prime() { }
    public void Read(SampleRow row) { }
    public void Dispose() { }
}

/// <summary>電源プラン。記録の先頭に書く（クロックの出方が変わるので、解析に要る）。名前は書かない（自分で付けた名前が入りうる）。</summary>
internal static class PowerPlan
{
    private static readonly (Guid Id, string Name)[] Known =
    {
        (new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "balanced"),
        (new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "high-performance"),
        (new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), "power-saver"),
        (new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), "ultimate-performance"),
    };

    /// <summary>"balanced" / "high-performance" / "power-saver" / "ultimate-performance" / "custom"（自作・メーカー）/ "unknown"。</summary>
    public static string Active()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return "unknown";
            try
            {
                var id = Marshal.PtrToStructure<Guid>(p);
                foreach (var (g, n) in Known) if (g == id) return n;
                return "custom";
            }
            finally { LocalFree(p); }
        }
        catch (EntryPointNotFoundException) { return "unknown"; }
        catch (DllNotFoundException) { return "unknown"; }
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr mem);
}
