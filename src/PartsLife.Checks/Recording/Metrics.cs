using System.Globalization;

namespace PartsLife.Checks.Recording;

/// <summary>
/// 項目ごとの「読めるか」。**3 つだけ**（色と言葉を増やすと、どれが本当に大事なのか読めなくなる）。
/// </summary>
public enum Availability
{
    /// <summary>この機械で、権限もドライバも足さずに読めた。</summary>
    Available,
    /// <summary>読めない。理由は <see cref="MetricStatus.Reason"/>。**値を推測では埋めない。**</summary>
    Unavailable,
    /// <summary>標準の読み方では読めない。追加の仕組み（ドライバ）が要る。この版では読まない（段 3）。</summary>
    NeedsExtension,
}

/// <summary>
/// 項目 1 つの判定。<see cref="Key"/> は記録の列の名前でもある（`cpu.load_pct`・`gpu0.temp_c` …）。
/// <see cref="Reason"/> は言語に依らない短い印（<see cref="Reasons"/>）。画面と書き出しで文にする。
/// </summary>
public sealed record MetricStatus(string Key, string Unit, string Source, Availability State, string Reason = "")
{
    public bool IsAvailable => State == Availability.Available;
}

/// <summary>読めない（または読み方に注意が要る）理由の印。文にするのは <see cref="RecordText"/>。</summary>
public static class Reasons
{
    /// <summary>CPU の温度・電圧・ファンなど。カーネルドライバ（PawnIO など）が要る。</summary>
    public const string NeedsDriver = "needs-driver";
    /// <summary>PDH のカウンタがこの Windows に無い。</summary>
    public const string NoCounter = "no-counter";
    /// <summary>カウンタはあるが、値が返らない（無効・対象なし）。</summary>
    public const string NoValue = "no-value";
    /// <summary>NVML が「このカードは非対応」と返した（ノート PC のファンなど）。</summary>
    public const string NotSupported = "not-supported";
    public const string NoPermission = "no-permission";
    /// <summary>NVIDIA のドライバ（nvml.dll）が見つからない・開けない。</summary>
    public const string NoNvml = "no-nvml";
    /// <summary>ドライバが古く、その関数が無い。</summary>
    public const string LibFunctionMissing = "lib-function-missing";
    /// <summary>AMD のドライバ（ADLX）が見つからない・開けない。</summary>
    public const string NoAdlx = "no-adlx";
    /// <summary>ADLX は開けたが、この GPU の項目を返さなかった。</summary>
    public const string AdlxNoMetric = "adlx-no-metric";
    /// <summary>この版はその会社のドライバ経由の読み取りを持たない（Intel の IGCL。段 3 以降）。</summary>
    public const string VendorNotImplemented = "vendor-not-implemented";
    public const string NoBattery = "no-battery";
    public const string AcUnknown = "ac-unknown";
    /// <summary>読み取りが決めた時間に戻らなかった。</summary>
    public const string Timeout = "timeout";
    /// <summary>値が現実的な範囲に無い（単位の取り違えを疑う）ので使わない。</summary>
    public const string Implausible = "implausible";
    public const string Failed = "failed";
    /// <summary>読めている。値は OS の報告で、推定を含むことがある（CPU の電力）。</summary>
    public const string OsReported = "os-reported";
}

/// <summary>項目の名前（列の名前）の決まり。受け手（ブラウザ側）と同時に作る約束なので、変えるときは形式の版を上げる。</summary>
public static class MetricKeys
{
    public const string CpuLoad = "cpu.load_pct";
    public const string CpuClock = "cpu.clock_mhz";
    public const string CpuPerfLimit = "cpu.perf_limit_pct";
    public const string CpuPerfLimitFlags = "cpu.perf_limit_flags";
    public const string CpuPackagePower = "cpu.pkg_power_w";
    public const string CpuTemp = "cpu.temp_c";
    public const string CpuVoltage = "cpu.vcore_v";
    public const string BoardFan = "board.fan_rpm";
    public const string BoardTemp = "board.temp_c";
    public const string MemUsedPct = "mem.used_pct";
    public const string MemUsedMb = "mem.used_mb";
    public const string DiskRead = "disk.read_mbps";
    public const string DiskWrite = "disk.write_mbps";
    public const string DiskBusy = "disk.busy_pct";
    public const string PowerAc = "power.ac";
    public const string PowerBattery = "power.battery_pct";

    // GPU の項目名（gpu0. の後ろ）
    public const string Util = "util_pct";
    public const string MemUsed = "mem_used_mb";
    public const string Temp = "temp_c";
    public const string Clock = "clock_mhz";
    public const string MemClock = "mem_clock_mhz";
    public const string Power = "power_w";
    public const string PowerLimit = "power_limit_w";
    public const string FanPct = "fan_pct";
    public const string FanRpm = "fan_rpm";
    public const string Throttle = "throttle";

    public static string Gpu(int index, string metric) => $"gpu{index.ToString(CultureInfo.InvariantCulture)}.{metric}";
    public static string Lp(int index, string metric) => $"cpu.lp{index.ToString(CultureInfo.InvariantCulture)}.{metric}";

    /// <summary>`gpu3.temp_c` → (3, "temp_c")。GPU の項目でなければ false。</summary>
    public static bool TryParseGpu(string key, out int index, out string metric)
    {
        index = 0; metric = "";
        if (!key.StartsWith("gpu", StringComparison.Ordinal)) return false;
        int dot = key.IndexOf('.');
        if (dot < 4 || dot == key.Length - 1) return false;
        if (!int.TryParse(key.AsSpan(3, dot - 3), NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
        metric = key[(dot + 1)..];
        return true;
    }

    /// <summary>論理プロセッサごとの項目（`cpu.lp12.load_pct`）。要約では個別に出さず、まとめて数える。</summary>
    public static bool IsPerLogicalProcessor(string key) =>
        key.StartsWith("cpu.lp", StringComparison.Ordinal) && key.Length > 6 && char.IsAsciiDigit(key[6]);

    /// <summary>その項目の単位（画面と書き出しに添える）。知らない項目は空。</summary>
    public static string UnitOf(string key)
    {
        string tail = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
        return tail switch
        {
            "load_pct" or "util_pct" or "used_pct" or "busy_pct" or "perf_limit_pct" or "fan_pct" or "battery_pct" => "%",
            "clock_mhz" or "mem_clock_mhz" => "MHz",
            "temp_c" => "°C",
            "power_w" or "pkg_power_w" or "power_limit_w" => "W",
            "vcore_v" => "V",
            "fan_rpm" => "rpm",
            "mem_used_mb" or "used_mb" => "MB",
            "read_mbps" or "write_mbps" => "MB/s",
            "ac" => "0/1",
            "throttle" or "perf_limit_flags" => "bitmask",
            _ => "",
        };
    }
}

/// <summary>GPU 1枚の素性（記録の中の番号 `gpu0` と、その実体）。順番は PCI のバス番号順。</summary>
public sealed record GpuDescriptor(int Index, string Name, string Vendor, long VramMb)
{
    public string Id => "gpu" + Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>検出の結果（開始のときに 1 回）。画面に見せ、記録の先頭にも書く。</summary>
public sealed class CapabilityReport
{
    public IReadOnlyList<MetricStatus> Metrics { get; }
    public IReadOnlyList<GpuDescriptor> Gpus { get; }
    public int LogicalProcessors { get; }

    public CapabilityReport(IReadOnlyList<MetricStatus> metrics, IReadOnlyList<GpuDescriptor> gpus, int logicalProcessors)
    {
        Metrics = metrics;
        Gpus = gpus;
        LogicalProcessors = logicalProcessors;
    }

    public int Count(Availability a) => Metrics.Count(m => m.State == a);

    /// <summary>画面に並べる行（論理プロセッサごとの項目は、使用率とクロックの 2 行にまとめる）。</summary>
    public IReadOnlyList<MetricStatus> Display()
    {
        var list = new List<MetricStatus>();
        bool loadDone = false, clockDone = false;
        foreach (var m in Metrics)
        {
            if (MetricKeys.IsPerLogicalProcessor(m.Key))
            {
                bool isLoad = m.Key.EndsWith(".load_pct", StringComparison.Ordinal);
                if (isLoad && !loadDone) { list.Add(m with { Key = "cpu.lp*.load_pct" }); loadDone = true; }
                else if (!isLoad && !clockDone) { list.Add(m with { Key = "cpu.lp*.clock_mhz" }); clockDone = true; }
                continue;
            }
            list.Add(m);
        }
        return list;
    }
}
