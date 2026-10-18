using System.Globalization;

namespace PartsLife.Checks.Recording;

/// <summary>
/// 記録の画面と書き出しの文言。言語は引数で受ける（"ja" / "en"）。ファイルや JSON に残す印（コード）はここで文にするだけで、
/// 印そのものは言語に依らない。調子は点検の窓に合わせる: 短く、言い切る。分からないことは分からないと言う。
/// </summary>
public static class RecordText
{
    private static string T(string lang, string ja, string en) => lang == "en" ? en : ja;

    public static string State(Availability a, string lang) => a switch
    {
        Availability.Available => T(lang, "読めます", "readable"),
        Availability.NeedsExtension => T(lang, "追加機能が必要", "needs extension"),
        _ => T(lang, "読めません", "not readable"),
    };

    public static string Reason(string code, string lang) => code switch
    {
        "" => "",
        Reasons.NeedsDriver => T(lang, "カーネルドライバが要るため、この版では読みません（追加機能で対応予定）",
                                       "needs a kernel driver, so this version does not read it (planned as an extension)"),
        Reasons.NoCounter => T(lang, "この Windows にそのカウンタがありません", "this Windows does not have that counter"),
        Reasons.NoValue => T(lang, "値が返りませんでした", "no value was returned"),
        Reasons.NotSupported => T(lang, "このカードは対応していません", "this card does not support it"),
        Reasons.NoPermission => T(lang, "権限が足りません", "not permitted"),
        Reasons.NoNvml => T(lang, "NVIDIA のドライバ（nvml.dll）を開けませんでした", "could not open the NVIDIA driver library (nvml.dll)"),
        Reasons.LibFunctionMissing => T(lang, "ドライバが古く、この項目を返せません", "the driver is too old to return this"),
        Reasons.NoAdlx => T(lang, "AMD のドライバ（ADLX）から読めませんでした", "could not read it through the AMD driver (ADLX)"),
        Reasons.AdlxNoMetric => T(lang, "ADLX がこの GPU のこの項目を返しませんでした", "ADLX did not return this item for this GPU"),
        Reasons.VendorNotImplemented => T(lang, "この版はこの会社の GPU の温度などを読みません（Intel は今後）",
                                                "this version does not read temperatures etc. for this vendor's GPUs (Intel: later)"),
        Reasons.NoBattery => T(lang, "電池がありません", "no battery"),
        Reasons.AcUnknown => T(lang, "電源の状態が分かりません", "power source unknown"),
        Reasons.Timeout => T(lang, "読み取りが時間内に戻りませんでした", "the read did not return in time"),
        Reasons.Implausible => T(lang, "値が現実的な範囲になく、単位の取り違えを疑うため使いません", "the value is out of any plausible range, so it is not used"),
        Reasons.Failed => T(lang, "読めませんでした", "could not be read"),
        Reasons.OsReported => T(lang, "OS の報告値（推定を含むことがあります）", "as reported by Windows (may include estimates)"),
        _ => code,
    };

    /// <summary>画面用の短い名前。GPU の番号は付ける（`GPU0`）。名前は <see cref="CapabilityReport.Gpus"/> で別に見せる。</summary>
    public static string Label(string key, string lang)
    {
        switch (key)
        {
            case MetricKeys.CpuLoad: return T(lang, "CPU 使用率（全体）", "CPU load (total)");
            case MetricKeys.CpuClock: return T(lang, "CPU 実効クロック", "CPU effective clock");
            case MetricKeys.CpuPerfLimit: return T(lang, "CPU 性能の制限（OS の報告）", "CPU performance limit (reported by Windows)");
            case MetricKeys.CpuPerfLimitFlags: return T(lang, "CPU 制限の印（OS の報告）", "CPU limit flags (reported by Windows)");
            case MetricKeys.CpuPackagePower: return T(lang, "CPU パッケージ電力", "CPU package power");
            case "cpu.lp*.load_pct": return T(lang, "論理プロセッサごとの使用率", "Per-logical-processor load");
            case "cpu.lp*.clock_mhz": return T(lang, "論理プロセッサごとの実効クロック", "Per-logical-processor effective clock");
            case MetricKeys.CpuTemp: return T(lang, "CPU 温度", "CPU temperature");
            case MetricKeys.CpuVoltage: return T(lang, "CPU コア電圧", "CPU core voltage");
            case MetricKeys.BoardFan: return T(lang, "ファン回転数", "Fan speed");
            case MetricKeys.BoardTemp: return T(lang, "マザーボード温度", "Motherboard temperature");
            case MetricKeys.MemUsedPct: return T(lang, "メモリ使用率", "Memory used");
            case MetricKeys.MemUsedMb: return T(lang, "メモリ使用量", "Memory used (MB)");
            case MetricKeys.DiskRead: return T(lang, "ディスク読み込み", "Disk read");
            case MetricKeys.DiskWrite: return T(lang, "ディスク書き込み", "Disk write");
            case MetricKeys.DiskBusy: return T(lang, "ディスクの忙しさ", "Disk busy");
            case MetricKeys.PowerAc: return T(lang, "電源（AC=1・電池=0）", "Power source (AC=1, battery=0)");
            case MetricKeys.PowerBattery: return T(lang, "電池の残り", "Battery level");
        }
        if (MetricKeys.TryParseGpu(key, out int gi, out string m))
        {
            string g = "GPU" + gi.ToString(CultureInfo.InvariantCulture);
            string what = m switch
            {
                MetricKeys.Util => T(lang, "使用率", "load"),
                MetricKeys.MemUsed => T(lang, "専用メモリ使用量", "dedicated memory used"),
                MetricKeys.Temp => T(lang, "温度", "temperature"),
                MetricKeys.Clock => T(lang, "クロック", "clock"),
                MetricKeys.MemClock => T(lang, "メモリクロック", "memory clock"),
                MetricKeys.Power => T(lang, "電力", "power"),
                MetricKeys.PowerLimit => T(lang, "電力の上限", "power limit"),
                MetricKeys.FanPct => T(lang, "ファン", "fan"),
                MetricKeys.FanRpm => T(lang, "ファン回転数", "fan speed"),
                MetricKeys.Throttle => T(lang, "制限の印", "limit flags"),
                _ => m,
            };
            return g + " " + what;
        }
        if (key.StartsWith("source.", StringComparison.Ordinal)) return T(lang, "読み口 ", "Source ") + key[7..];
        return key;
    }

    /// <summary>AI に渡す文書の「フィールドの意味」。キーの末尾で引く。</summary>
    public static string Meaning(string key, string lang)
    {
        if (MetricKeys.IsPerLogicalProcessor(key)) return "";
        string tail = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
        bool gpuNote = key.StartsWith("gpu", StringComparison.Ordinal);
        return tail switch
        {
            "load_pct" => T(lang, "CPU の使用率（%）。Windows の「% Processor Time」。全論理プロセッサの平均", "CPU load in percent (Windows \"% Processor Time\"), averaged over all logical processors"),
            "clock_mhz" when key == MetricKeys.CpuClock =>
                T(lang, "CPU の実効クロック（MHz）。基準クロック × 性能比（% Processor Performance）。ブーストを含む。特定のコアの測定値ではなく OS が性能の比から出した値",
                        "CPU effective clock in MHz = base clock x Windows \"% Processor Performance\"; includes boost; derived by Windows, not a per-core measurement"),
            "clock_mhz" => T(lang, "GPU のグラフィックスクロック（MHz）", "GPU graphics clock in MHz"),
            "mem_clock_mhz" => T(lang, "GPU のメモリクロック（MHz）。ドライバの報告値で、世代によって数え方が違う", "GPU memory clock in MHz as reported by the driver; how it is counted differs between GPU generations"),
            "perf_limit_pct" => T(lang, "OS が報告する CPU 性能の制限（%）。100 未満は何らかの理由で性能が抑えられていると OS が報告したこと（理由は不明）。論理プロセッサのうち最も低い値",
                                        "performance limit reported by Windows in percent; below 100 means Windows reports the CPU is being held back (reason unknown); lowest across logical processors"),
            "perf_limit_flags" => T(lang, "OS が報告する性能制限の印（bit の和集合）。意味は解読していない。0 以外なら何かの印が立っていた",
                                          "performance-limit flags reported by Windows (bitwise OR over logical processors); bits are not decoded; non-zero means some flag was set"),
            "pkg_power_w" => T(lang, "CPU パッケージの電力（W）。Windows のエネルギーメーター（RAPL 相当）の報告値で、推定を含むことがある。読めない PC もある",
                                     "CPU package power in watts as reported by the Windows Energy Meter (RAPL-like); may include estimates; not available on every PC"),
            "temp_c" when gpuNote => T(lang, "GPU の温度（℃）。ドライバが報告する GPU 温度", "GPU temperature in Celsius as reported by the driver"),
            "power_w" => T(lang, "GPU の電力（W）。ドライバが報告するボード側の消費電力", "GPU power draw in watts as reported by the driver"),
            "power_limit_w" => T(lang, "GPU の電力の上限（W）。ドライバが現在適用している値", "GPU power limit in watts currently enforced by the driver"),
            "util_pct" => T(lang, "GPU の使用率（%）。NVIDIA はドライバの値、それ以外は Windows の GPU Engine カウンタ（3D・コピーなどエンジン種別ごとの合計の最大）",
                                  "GPU load in percent. NVIDIA: driver-reported; others: Windows GPU Engine counters (max over engine types of the per-type sum)"),
            "mem_used_mb" when gpuNote => T(lang, "GPU の専用メモリ使用量（MB）", "GPU dedicated memory used in MB"),
            "used_pct" => T(lang, "搭載メモリの使用率（%）", "physical memory used in percent"),
            "used_mb" or "mem_used_mb" => T(lang, "搭載メモリの使用量（MB）", "physical memory used in MB"),
            "fan_pct" => T(lang, "GPU ファンの回転割合（%）。ドライバの報告値（最大に対する割合）", "GPU fan speed in percent of max, as reported by the driver"),
            "fan_rpm" => T(lang, "ファン回転数（rpm）", "fan speed in rpm"),
            "throttle" => T(lang, "ドライバが返す「クロックを下げている理由」の bit。要約の limits に名前にして集計してある", "driver-reported clock-reduction reason bits; aggregated by name in `limits`"),
            "read_mbps" => T(lang, "ディスクの読み込み（MB/秒、全ディスクの合計）", "disk read MB/s over all disks"),
            "write_mbps" => T(lang, "ディスクの書き込み（MB/秒、全ディスクの合計）", "disk write MB/s over all disks"),
            "busy_pct" => T(lang, "ディスクが処理中だった割合（%）", "share of time the disks were busy, in percent"),
            "ac" => T(lang, "電源。1 = AC、0 = 電池", "power source: 1 = AC, 0 = battery"),
            "battery_pct" => T(lang, "電池の残り（%）", "battery level in percent"),
            _ => "",
        };
    }

    public static string LimitName(string name, string lang) => name switch
    {
        "gpu-idle" => T(lang, "GPU が待機中", "GPU idle"),
        "applications-clocks-setting" => T(lang, "アプリケーションがクロックを指定", "clocks set by an application"),
        "sw-power-cap" => T(lang, "電力の上限（ソフトウェア）", "software power cap"),
        "hw-slowdown" => T(lang, "ハードウェアによる速度低下", "hardware slowdown"),
        "sync-boost" => T(lang, "複数 GPU のクロック同期", "sync boost"),
        "sw-thermal-slowdown" => T(lang, "温度による低下（ソフトウェア）", "software thermal slowdown"),
        "hw-thermal-slowdown" => T(lang, "温度による低下（ハードウェア）", "hardware thermal slowdown"),
        "hw-power-brake-slowdown" => T(lang, "電源ブレーキによる低下（ハードウェア）", "hardware power-brake slowdown"),
        "display-clock-setting" => T(lang, "ディスプレイのクロック設定", "display clock setting"),
        "os-performance-limit-below-100" => T(lang, "OS が性能を 100% 未満に制限", "Windows limited performance below 100%"),
        "os-performance-limit-flags-set" => T(lang, "OS の性能制限の印が立っていた", "Windows performance-limit flags were set"),
        _ => name,
    };

    public static string Marker(MarkerKind k, string lang) => k switch
    {
        MarkerKind.BenchStart => T(lang, "ベンチ開始", "Bench start"),
        MarkerKind.BenchEnd => T(lang, "ベンチ終了", "Bench end"),
        MarkerKind.StressStart => T(lang, "ストレス開始", "Stress start"),
        MarkerKind.StressEnd => T(lang, "ストレス終了", "Stress end"),
        _ => T(lang, "目印", "Mark"),
    };

    /// <summary>要約の注記を 1 文に。<paramref name="lang"/> が "en" なら英語（AI に渡す書き出しも英語の文を添える）。</summary>
    public static string Note(SummaryNote n, string lang)
    {
        string subject = SubjectName(n.Subject, lang);
        switch (n.Code)
        {
            case "no-samples":
                return T(lang, "記録に値が入っていませんでした", "The recording contains no samples");
            case "too-short":
                return T(lang, $"記録が {n.Detail} 秒と短いため、持続した値は出していません（60 秒以上で出します）",
                               $"The recording is only {n.Detail} s, so sustained values are not given (they need 60 s or more)");
            case "sparse":
                return T(lang, $"{subject} は記録の {n.Detail}% の間しか読めていません", $"{subject} could be read for only {n.Detail}% of the recording");
            case "no-sustained-busy":
                return T(lang, $"{subject} は負荷が高かった時間が短い、または途切れているため、持続した値は出していません",
                               $"{subject} was not under high load for long enough, so no sustained values are given");
            case "device-idle":
                return T(lang, $"{subject} はほとんど使われていません（使用率の最大 {n.Detail}%）。この記録は {subject} の温度やクロックを比べるのには向きません",
                               $"{subject} was barely used (max load {n.Detail}%); this recording is not suitable for judging its temperature or clocks");
            case "unavailable":
            {
                int colon = n.Detail.IndexOf(':');
                string state = colon < 0 ? n.Detail : n.Detail[..colon];
                string reason = colon < 0 ? "" : n.Detail[(colon + 1)..];
                string st = State(SessionJson.ParseState(state), lang);
                return $"{Label(n.Subject, lang)}（{n.Subject}）: {st}" + (reason.Length > 0 ? " — " + Reason(reason, lang) : "");
            }
            case "battery-used":
                return T(lang, "電池で動いた時間があります。電池駆動では、クロックや電力が抑えられることがあります",
                               "The PC ran on battery for part of the recording; clocks and power may be held back on battery");
            case "gaps":
            {
                var p = n.Detail.Split('/');
                return T(lang, $"記録に空きがあります（合計 {p[0]} 秒・最長 {(p.Length > 1 ? p[1] : "?")} 秒）。PC の負荷やスリープの影響かもしれません",
                               $"The recording has gaps (total {p[0]} s, longest {(p.Length > 1 ? p[1] : "?")} s), possibly from system load or sleep");
            }
            case "source-timeouts":
                return T(lang, $"{n.Subject} の読み取りが {n.Detail} 回、時間内に戻りませんでした", $"Reading {n.Subject} did not return in time {n.Detail} time(s)");
            case "not-completed":
                return T(lang, $"記録は正常には終わっていません（{n.Subject}{(n.Detail.Length > 0 ? "・" + n.Detail : "")}）。ここまでの記録で要約しています",
                               $"The recording did not end normally ({n.Subject}{(n.Detail.Length > 0 ? ", " + n.Detail : "")}); summarised from what was written");
            case "cpu-clock-drop-no-temp":
            {
                var p = n.Detail.Split('/');
                return T(lang, $"CPU の持続したクロック（{p[0]} MHz）が最大（{(p.Length > 1 ? p[1] : "?")} MHz）より低いですが、CPU の温度が読めないため、理由は切り分けられません",
                               $"CPU sustained clock ({p[0]} MHz) is below its peak ({(p.Length > 1 ? p[1] : "?")} MHz), but CPU temperature is not readable, so the cause cannot be narrowed down");
            }
            case "marker-unpaired":
                return T(lang, $"対になる開始の印が無い終了の印がありました（{n.Subject}）", $"An end mark without a matching start mark was found ({n.Subject})");
            case "name-withheld":
                return T(lang, "個人を特定しうる文字を含む名前は、書き出しから外しました", "A name that could identify the user was left out of the export");
            default:
                return n.Code + (n.Subject.Length > 0 ? " " + n.Subject : "");
        }
    }

    /// <summary>
    /// 同時に起きた時間帯の 1 行。**「同じ時間帯に起きた」と書き、原因は言わない。**
    /// </summary>
    public static string CoOccurrence(CoOccurrence c, string lang)
    {
        string dev = SubjectName(c.Device, lang);
        string n(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        return T(lang,
            $"{dev}: {n(c.StartSec)}〜{n(c.EndSec)} 秒のあいだ、温度が {n(c.TempFromC)}→{n(c.TempToC)} ℃に上がり、クロックが {n(c.ClockFromMHz)}→{n(c.ClockToMHz)} MHz に下がっていました（同じ時間帯に起きたことで、原因は分かりません）",
            $"{dev}: from {n(c.StartSec)} to {n(c.EndSec)} s, temperature rose {n(c.TempFromC)}->{n(c.TempToC)} C while the clock fell {n(c.ClockFromMHz)}->{n(c.ClockToMHz)} MHz (these happened in the same time window; the cause cannot be told)");
    }

    /// <summary>"gpu0" → "GPU0"、"cpu" → "CPU"、キー（cpu.temp_c など）はそのまま名前に。</summary>
    private static string SubjectName(string subject, string lang)
    {
        if (subject == "cpu") return "CPU";
        if (subject.StartsWith("gpu", StringComparison.Ordinal) && !subject.Contains('.')) return "GPU" + subject[3..];
        return subject.Contains('.') ? Label(subject, lang) : subject;
    }
}
