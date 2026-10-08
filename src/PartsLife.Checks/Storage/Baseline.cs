using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartsLife.Checks.Storage;

/// <summary>ある時点の数え上げ（台ごと）。</summary>
public sealed class BaselineEntry
{
    public DateTimeOffset At { get; set; }
    public Dictionary<string, ulong> Counters { get; set; } = new();
}

/// <summary>1台ぶんの履歴。ファイル1つに1台。</summary>
public sealed class BaselineHistory
{
    public string Key { get; set; } = "";
    public string Model { get; set; } = "";
    public List<BaselineEntry> Entries { get; set; } = new();
}

/// <summary>前回からの変化1件。</summary>
public sealed record CounterDelta(string Counter, ulong Before, ulong After, DateTimeOffset Since)
{
    public long Change => After >= Before ? (long)Math.Min(After - Before, long.MaxValue)
                                          : -(long)Math.Min(Before - After, long.MaxValue);
    public bool Grew => After > Before;

    /// <summary>「メディアエラー 363,278 → 363,301 (+23)」</summary>
    public string Format() =>
        $"{Counters.Label(Counter)} {L.N(Before)} → {L.N(After)} ({(Change >= 0 ? "+" : "")}{L.N(Change)})";
}

/// <summary>
/// 数え上げの意味。**増えたら何を意味するか**を1か所に置く。
/// </summary>
public static class Counters
{
    public const string MediaErrors = "nvme.media_errors";
    public const string ErrorLogEntries = "nvme.error_log_entries";
    public const string UnsafeShutdowns = "nvme.unsafe_shutdowns";
    public const string PowerOnHours = "nvme.power_on_hours";
    public const string DataUnitsWritten = "nvme.data_units_written";
    public const string PercentageUsed = "nvme.percentage_used";
    public const string AvailableSpare = "nvme.available_spare";
    public const string CriticalWarning = "nvme.critical_warning";

    public static string Ata(byte id) => $"ata.{id}";

    /// <summary>増えたときの重さ。null は「増えて当たり前」（通電時間など）。</summary>
    public static Verdict? GrowthVerdict(string counter) => counter switch
    {
        MediaErrors => Verdict.Check,
        ErrorLogEntries => Verdict.Caution,
        CriticalWarning => Verdict.Check,
        "ata.5" or "ata.196" or "ata.197" or "ata.198" or "ata.187" => Verdict.Check,
        "ata.199" => Verdict.Caution,          // CRC はケーブルや差し込みのことが多い
        _ => null,
    };

    public static string Label(string counter) => counter switch
    {
        MediaErrors => L.T("メディアエラー", "Media errors"),
        ErrorLogEntries => L.T("エラーログ件数", "Error log entries"),
        UnsafeShutdowns => L.T("不意の電源断", "Unsafe shutdowns"),
        PowerOnHours => L.T("通電時間", "Power-on hours"),
        DataUnitsWritten => L.T("書き込み量（単位）", "Data units written"),
        PercentageUsed => L.T("寿命の消費 %", "Percentage used"),
        AvailableSpare => L.T("予備領域 %", "Available spare"),
        CriticalWarning => L.T("重大警告ビット", "Critical warning bits"),
        _ when counter.StartsWith("ata.", StringComparison.Ordinal) && byte.TryParse(counter[4..], out var id)
            => SmartNames.Of(id),
        _ => counter,
    };

    /// <summary>読み取り結果から、覚えておく数え上げを取り出す。</summary>
    public static Dictionary<string, ulong> From(DiskReport r)
    {
        var c = new Dictionary<string, ulong>();
        if (r.Nvme is { } n)
        {
            c[MediaErrors] = n.MediaErrors;
            c[ErrorLogEntries] = n.ErrorLogEntries;
            c[UnsafeShutdowns] = n.UnsafeShutdowns;
            c[PowerOnHours] = n.PowerOnHours;
            c[DataUnitsWritten] = n.DataUnitsWritten;
            c[PercentageUsed] = n.PercentageUsed;
            c[AvailableSpare] = n.AvailableSpare;
            c[CriticalWarning] = n.CriticalWarning;
        }
        foreach (var a in r.Ata)
        {
            if (AtaSmart.KeyIds.Contains(a.Id) || a.Id is 9 or 194 or 190)
                c[Ata(a.Id)] = a.Value;
        }
        return c;
    }
}

/// <summary>
/// 基準値の保存と差分。
///
/// ■ 置き場所は %APPDATA%\PartsLife\checks\。**本体（利用者の権限）が書く。**
///   管理者で動く補助 exe には書かせない。別の利用者の資格情報で昇格した場合、
///   補助 exe の %APPDATA% は別人の物になり、記録が迷子になるため。
/// ■ 台はシリアル番号で見分ける。
/// ■ 数字そのものより**前回からの増え方**を見る。累計が大きくても増えていなければ過去の話。
/// </summary>
public sealed class BaselineStore
{
    public const int MaxEntries = 200;
    private readonly string _dir;

    public BaselineStore(string? directory = null)
    {
        _dir = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartsLife", "checks");

    public string Directory => _dir;

    public string PathFor(string key) => Path.Combine(_dir, "disk-" + SafeName(key) + ".json");

    /// <summary>ファイル名に使えない文字を潰す。シリアルに空白や記号が入る機種がある。</summary>
    public static string SafeName(string key)
    {
        var chars = key.Trim().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        string s = new string(chars).Trim('_');
        if (s.Length == 0) s = "unknown";
        return s.Length > 80 ? s[..80] : s;
    }

    public BaselineHistory Load(string key)
    {
        try
        {
            string p = PathFor(key);
            if (File.Exists(p))
            {
                var h = JsonSerializer.Deserialize(File.ReadAllText(p), CheckJson.Default.BaselineHistory);
                if (h is not null && h.Key == key) return h;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new BaselineHistory { Key = key };
    }

    /// <summary>今回の値を足して保存し、**足す前の最後の記録**を返す（初回は null）。</summary>
    public BaselineEntry? Record(DiskReport report, DateTimeOffset now)
    {
        var history = Load(report.Key);
        var previous = history.Entries.Count > 0 ? history.Entries[^1] : null;
        history.Model = report.Model;
        history.Entries.Add(new BaselineEntry { At = now, Counters = Counters.From(report) });
        if (history.Entries.Count > MaxEntries)
            history.Entries.RemoveRange(0, history.Entries.Count - MaxEntries);

        System.IO.Directory.CreateDirectory(_dir);
        string path = PathFor(report.Key);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(history, CheckJson.Default.BaselineHistory));
        File.Move(tmp, path, overwrite: true);
        return previous;
    }

    /// <summary>前回と今回の差。前回に無かった数え上げは出さない（比べようがない）。</summary>
    public static IReadOnlyList<CounterDelta> Diff(BaselineEntry? previous, IReadOnlyDictionary<string, ulong> current)
    {
        if (previous is null) return Array.Empty<CounterDelta>();
        var list = new List<CounterDelta>();
        foreach (var (k, v) in current)
        {
            if (previous.Counters.TryGetValue(k, out var before))
                list.Add(new CounterDelta(k, before, v, previous.At));
        }
        return list;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BaselineHistory))]
internal partial class CheckJson : JsonSerializerContext { }
