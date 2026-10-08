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

/// <summary>基準値と比べられたか。比べられなかったときは**増えたとは言わない。**</summary>
public enum BaselineState
{
    /// <summary>前回の記録と比べた。</summary>
    Compared,
    /// <summary>初めての記録。増えているかは次回分かる。</summary>
    First,
    /// <summary>シリアルも一意な識別子も無い。同じ型番・容量の別の台と区別できないので、記録も比較もしない。</summary>
    NoIdentity,
    /// <summary>同じ識別子の台が同時に複数あり、接続の位置でも見分けられなかった。</summary>
    Duplicate,
    /// <summary>通電時間・書き込み量などの減らないはずの数が前回より減っている（別の台の記録の可能性）。</summary>
    Mismatch,
    /// <summary>
    /// 通電時間が、前回の記録から経った時間より多く進んでいる（別の台の記録の可能性）。
    /// 同じシリアルの台が接続の位置を入れ替えた・同じシリアルの別の台に替わった、のどちらでも起きる。
    /// </summary>
    AheadOfClock,
    /// <summary>以前の形式（v1）の記録が、この台だけの物だと確かめられなかったので引き継がなかった。v1 のファイルは残す。</summary>
    LegacyRefused,
    /// <summary>読めなかった / 記録に失敗した。</summary>
    NotRecorded,
}

public static class BaselineStates
{
    /// <summary>比べられなかった理由（利用者に見せる文）。比べた / 初回なら null。</summary>
    public static string? WhyNotCompared(BaselineState s) => s switch
    {
        BaselineState.NoIdentity => L.T("比べられない（識別子なし）", "cannot compare (no identifier)"),
        BaselineState.Duplicate => L.T("比べられない（同じ識別子の台が複数）", "cannot compare (several disks share one identifier)"),
        BaselineState.Mismatch => L.T("比べられない（前回の記録より通電時間か書き込み量が減っている。別の台の記録の可能性）",
                                      "cannot compare (power-on hours or data written went backwards; the last record may be another disk's)"),
        BaselineState.AheadOfClock => L.T("比べられない（前回の記録から経った時間より通電時間が多く進んでいる。別の台の記録の可能性）",
                                          "cannot compare (power-on hours advanced more than the time since the last record; the last record may be another disk's)"),
        BaselineState.LegacyRefused => L.T("比べられない（以前の記録に別の台の分が混ざっている可能性があるので引き継がなかった）",
                                           "cannot compare (the old record may include another disk's entries, so it was not carried over)"),
        _ => null,
    };
}

/// <summary>1台ぶんの記録の結果。</summary>
public sealed record BaselineOutcome(BaselineState State, BaselineEntry? Previous, string? Error = null);

/// <summary>
/// 基準値を台ごとに分ける鍵。
///
/// ■ 鍵はシリアルだけにしない
/// 同じシリアルを返す台（安い SSD・変換基板・RAID の構成員）や、シリアルを返さない同型の台がある。
/// それを1つの履歴にまとめると、A を B の最後の記録と比べてしまい、
/// 健康な台に毎回「+363,278 増えています」と出る。そこで
/// **シリアル + 一意な識別子（EUI-64 / NGUID / WWN）+ 型番 + 容量 + 接続**を鍵にする。
/// ■ シリアルも一意な識別子も無ければ「弱い」
/// 型番と容量だけでは同型の別の台と区別できない。弱い鍵では記録も比較もしない。
/// ■ ファイル名は鍵のハッシュ
/// 読める部分（シリアルか型番）は先頭に付けるだけ。記号を潰した結果が同じになる別のシリアルでも、
/// ハッシュが違うので別のファイルになる。
/// </summary>
public sealed record BaselineIdentity(string Key, string Label, bool Strong)
{
    public const string Version = "v2";

    public static BaselineIdentity Of(DiskReport r, bool withLocation = false)
    {
        string serial = r.Serial.Trim();
        bool strong = UsableSerial(serial) || r.UniqueId.Length > 0;
        var key = new System.Text.StringBuilder(Version)
            .Append("|sn=").Append(Esc(serial))
            .Append("|uid=").Append(Esc(r.UniqueId))
            .Append("|model=").Append(Esc(r.Model.Trim()))
            .Append("|size=").Append(r.SizeBytes)
            .Append("|bus=").Append(r.Bus);
        if (withLocation) key.Append("|loc=").Append(Esc(r.Location));
        string label = UsableSerial(serial) ? serial : r.Model;
        return new BaselineIdentity(key.ToString(), label, strong);
    }

    /// <summary>"000000000000" や空白だけのシリアルは「無い」扱い（そう返す変換基板がある）。</summary>
    public static bool UsableSerial(string serial) =>
        serial.Any(ch => char.IsAsciiLetterOrDigit(ch) && ch != '0');

    private static string Esc(string s) => s.Replace("%", "%25").Replace("|", "%7C");

    /// <summary>"disk-&lt;読める部分&gt;-&lt;鍵の SHA-256 の先頭 16 桁&gt;.json"</summary>
    public string FileName
    {
        get
        {
            string prefix = BaselineStore.SafeName(Label);
            if (prefix.Length > 40) prefix = prefix[..40];
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Key));
            return $"disk-{prefix}-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}.json";
        }
    }
}

/// <summary>
/// 基準値の保存と差分。
///
/// ■ 置き場所は %APPDATA%\PartsLife\checks\。**本体（利用者の権限）が書く。**
///   管理者で動く補助 exe には書かせない。別の利用者の資格情報で昇格した場合、
///   補助 exe の %APPDATA% は別人の物になり、記録が迷子になるため。
/// ■ 台の見分け方は <see cref="BaselineIdentity"/>。同じ鍵の台が同時に複数あれば接続の位置を足して分け、
///   それでも分けられなければ比べない。一度でも重なった鍵は twins.json に覚え、以後は位置付きで引く
///   （片方だけ繋いだ回に、もう片方の記録と比べないため）。
/// ■ 数字そのものより**前回からの増え方**を見る。累計が大きくても増えていなければ過去の話。
/// </summary>
public sealed class BaselineStore
{
    public const int MaxEntries = 200;
    private const string TwinsFile = "twins.json";
    private readonly string _dir;

    public BaselineStore(string? directory = null)
    {
        _dir = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartsLife", "checks");

    public string Directory => _dir;

    public string PathFor(BaselineIdentity id) => Path.Combine(_dir, id.FileName);

    /// <summary>v1（シリアルだけで引いていた頃）のファイルの場所。移し替えにだけ使う。</summary>
    internal string LegacyPathFor(string serial) => Path.Combine(_dir, "disk-" + SafeName(serial) + ".json");

    /// <summary>ファイル名に使えない文字を潰す。シリアルに空白や記号が入る機種がある。</summary>
    public static string SafeName(string key)
    {
        var chars = key.Trim().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        string s = new string(chars).Trim('_');
        if (s.Length == 0) s = "unknown";
        return s.Length > 80 ? s[..80] : s;
    }

    public BaselineHistory Load(DiskReport report) => Load(BaselineIdentity.Of(report));

    public BaselineHistory Load(BaselineIdentity id)
    {
        var h = TryRead(PathFor(id));
        return h is not null && h.Key == id.Key ? h : new BaselineHistory { Key = id.Key };
    }

    private static BaselineHistory? TryRead(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), CheckJson.Default.BaselineHistory);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>1台だけ記録する（同時に繋がっている台が分からないので、重なりの見分けは効かない）。</summary>
    public BaselineEntry? Record(DiskReport report, DateTimeOffset now) => RecordAll(new[] { report }, now)[0].Previous;

    /// <summary>
    /// 今回読めた台を全部まとめて記録し、台ごとに**足す前の最後の記録**（比べられれば）を返す。
    /// 読めなかった台（Unavailable）は記録しないが、重なりの見分けには数える。
    /// </summary>
    public IReadOnlyList<BaselineOutcome> RecordAll(IReadOnlyList<DiskReport> reports, DateTimeOffset now)
    {
        var plan = Plan(reports);
        var outcomes = new BaselineOutcome[reports.Count];
        for (int i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            var (id, state) = plan[i];
            if (r.Unavailable is not null) { outcomes[i] = new BaselineOutcome(BaselineState.NotRecorded, null); continue; }
            if (id is null) { outcomes[i] = new BaselineOutcome(state, null); continue; }
            try
            {
                var migration = id.Key.Contains("|loc=", StringComparison.Ordinal)
                    ? Migration.None : MigrateLegacy(r, id, reports, now);
                outcomes[i] = RecordOne(r, id, now, legacyRefused: migration == Migration.Refused);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes[i] = new BaselineOutcome(BaselineState.NotRecorded, null, ex.Message);
            }
        }
        return outcomes;
    }

    /// <summary>台ごとの鍵を決める。弱い鍵・見分けられない重なりには鍵を渡さない（null）。</summary>
    internal (BaselineIdentity? Id, BaselineState State)[] Plan(IReadOnlyList<DiskReport> reports)
    {
        var plain = reports.Select(r => BaselineIdentity.Of(r)).ToArray();
        var result = new (BaselineIdentity?, BaselineState)[reports.Count];
        var twins = LoadTwins();
        bool twinsChanged = false;

        foreach (var group in Enumerable.Range(0, reports.Count).Where(i => plain[i].Strong).GroupBy(i => plain[i].Key))
        {
            var idx = group.ToList();
            if (idx.Count > 1 && twins.Add(group.Key)) twinsChanged = true;
            if (idx.Count == 1 && !twins.Contains(group.Key))
            {
                result[idx[0]] = (plain[idx[0]], BaselineState.First);
                continue;
            }
            // 重なった（今回、または過去に）鍵は接続の位置で分ける。位置が無いか同じなら比べない
            var located = idx.Select(i => reports[i].Location).ToList();
            bool separable = located.All(l => l.Length > 0) && located.Distinct(StringComparer.Ordinal).Count() == located.Count;
            foreach (int i in idx)
                result[i] = separable ? (BaselineIdentity.Of(reports[i], withLocation: true), BaselineState.First)
                                      : (null, BaselineState.Duplicate);
        }
        for (int i = 0; i < reports.Count; i++)
            if (!plain[i].Strong) result[i] = (null, BaselineState.NoIdentity);

        if (twinsChanged) SaveTwins(twins);
        return result;
    }

    private BaselineOutcome RecordOne(DiskReport report, BaselineIdentity id, DateTimeOffset now, bool legacyRefused = false)
    {
        var history = Load(id);
        var previous = history.Entries.Count > 0 ? history.Entries[^1] : null;
        var current = Counters.From(report);
        history.Model = report.Model;
        history.Entries.Add(new BaselineEntry { At = now, Counters = current });
        if (history.Entries.Count > MaxEntries)
            history.Entries.RemoveRange(0, history.Entries.Count - MaxEntries);
        Save(PathFor(id), history);

        if (previous is null)
            return new BaselineOutcome(legacyRefused ? BaselineState.LegacyRefused : BaselineState.First, null);
        return CheckContinuity(previous, current, now) switch
        {
            Continuity.WentBackwards => new BaselineOutcome(BaselineState.Mismatch, null),
            Continuity.AheadOfClock => new BaselineOutcome(BaselineState.AheadOfClock, null),
            _ => new BaselineOutcome(BaselineState.Compared, previous),
        };
    }

    /// <summary>前回の記録が、今回読んだ台の物と言えるか。</summary>
    internal enum Continuity
    {
        Same,
        /// <summary>減らないはずの数が減った。</summary>
        WentBackwards,
        /// <summary>通電時間が、記録の間に経った時間より多く進んだ。</summary>
        AheadOfClock,
    }

    /// <summary>減らないはずの数（通電時間・書き込み量）。</summary>
    private static readonly string[] Monotonic = { Counters.PowerOnHours, Counters.DataUnitsWritten, Counters.Ata(9) };

    /// <summary>
    /// 時間で数える数。ATA の 9 は機種によって分や秒で数える物がある（古い HDD に少数）。
    /// その台は毎回「比べられない」になるが、**別の台の記録と比べて「増えています」と言うよりは良い**。
    /// </summary>
    private static readonly string[] Hours = { Counters.PowerOnHours, Counters.Ata(9) };

    /// <summary>通電時間は整数の時間で切り捨てられ、更新も間をおいて行われるので、そのぶんの余裕。</summary>
    internal static readonly TimeSpan HoursSlack = TimeSpan.FromHours(2);

    /// <summary>時計のずれの余裕（記録した時刻はこの機械の時計。起動時や NTP の補正で前後する）。</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    /// <summary>
    /// 前回の記録が別の台の物ではないかを確かめる。
    /// - 減らないはずの数（通電時間・書き込み量）が減った → 別の台
    /// - 通電時間が、前回の記録から経った時間（+ 余裕）より多く進んだ → 別の台
    ///   （同じシリアルの2台が位置を入れ替えた・同じシリアルの別の台に替わった。通電時間の長い方が
    ///   短い方の記録の位置に来ると、減ってはいないので上だけでは見抜けない）
    /// </summary>
    internal static Continuity CheckContinuity(BaselineEntry previous, IReadOnlyDictionary<string, ulong> current, DateTimeOffset now)
    {
        foreach (var k in Monotonic)
            if (previous.Counters.TryGetValue(k, out var before) && current.TryGetValue(k, out var after) && after < before)
                return Continuity.WentBackwards;

        double elapsed = Math.Max(0, (now - previous.At).TotalHours);
        double allowed = elapsed + HoursSlack.TotalHours + ClockSkew.TotalHours;
        foreach (var k in Hours)
            if (previous.Counters.TryGetValue(k, out var before) && current.TryGetValue(k, out var after) && after - before > allowed)
                return Continuity.AheadOfClock;
        return Continuity.Same;
    }

    /// <summary>2つの記録に、同じ台かを確かめられる数（通電時間・書き込み量）が共通してあるか。</summary>
    private static bool CanTell(IReadOnlyDictionary<string, ulong> a, IReadOnlyDictionary<string, ulong> b) =>
        Monotonic.Any(k => a.ContainsKey(k) && b.ContainsKey(k));

    /// <summary>
    /// 履歴が1台だけの物に見えるか。隣り合う記録どうしが <see cref="CheckContinuity"/> で食い違わず、
    /// 確かめられる数が共通してあること。v1 では同じシリアルの2台の記録が交互に混ざっていることがある。
    /// </summary>
    internal static bool LooksLikeOneDisk(IReadOnlyList<BaselineEntry> entries)
    {
        for (int i = 1; i < entries.Count; i++)
        {
            if (!CanTell(entries[i - 1].Counters, entries[i].Counters)) return false;
            if (CheckContinuity(entries[i - 1], entries[i].Counters, entries[i].At) != Continuity.Same) return false;
        }
        return true;
    }

    private enum Migration { None, Migrated, Refused }

    /// <summary>
    /// v1 の記録（ファイル名 = シリアル）を新しい鍵へ移す。**確かに同じ台だと言えるときだけ:**
    /// シリアルが使える値で、今回そのシリアル（とファイル名の潰れ方）が1台だけ、
    /// 中身の鍵と型番が一致し、新しい鍵の記録がまだ無い。移した元は .migrated に改名して残す。
    /// さらに、v1 の履歴の中で通電時間・書き込み量が食い違わず（<see cref="LooksLikeOneDisk"/>）、
    /// 今回の値がその最後の記録に続いていること。v1 では同じシリアルの2台の記録が交互に混ざっていることがあり、
    /// 片方だけ繋いだ回に移すと、最後の記録がもう片方の物かもしれない。
    /// そのときは**移さない**（Refused）。v1 のファイルはそのまま残し、新しい鍵の記録は今回から始める。
    /// </summary>
    private Migration MigrateLegacy(DiskReport r, BaselineIdentity id, IReadOnlyList<DiskReport> all, DateTimeOffset now)
    {
        string serial = r.Serial;
        if (!BaselineIdentity.UsableSerial(serial)) return Migration.None;
        string target = PathFor(id);
        if (File.Exists(target)) return Migration.None;
        string legacy = LegacyPathFor(serial);
        if (!File.Exists(legacy)) return Migration.None;
        string safe = SafeName(serial);
        if (all.Count(o => SafeName(o.Serial) == safe) != 1) return Migration.None;
        var old = TryRead(legacy);
        if (old is null || old.Key != serial || old.Model != r.Model || old.Entries.Count == 0) return Migration.None;

        var current = Counters.From(r);
        if (!LooksLikeOneDisk(old.Entries)
            || !CanTell(old.Entries[^1].Counters, current)
            || CheckContinuity(old.Entries[^1], current, now) != Continuity.Same)
            return Migration.Refused;

        Save(target, new BaselineHistory { Key = id.Key, Model = old.Model, Entries = old.Entries });
        File.Move(legacy, legacy + ".migrated", overwrite: true);
        return Migration.Migrated;
    }

    private HashSet<string> LoadTwins()
    {
        try
        {
            string p = Path.Combine(_dir, TwinsFile);
            if (File.Exists(p) && JsonSerializer.Deserialize(File.ReadAllText(p), CheckJson.Default.ListString) is { } list)
                return new HashSet<string>(list, StringComparer.Ordinal);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private void SaveTwins(HashSet<string> twins)
    {
        try
        {
            Save(Path.Combine(_dir, TwinsFile), twins.Order(StringComparer.Ordinal).ToList());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Save(string path, BaselineHistory h) =>
        WriteAtomically(path, JsonSerializer.Serialize(h, CheckJson.Default.BaselineHistory));

    private void Save(string path, List<string> list) =>
        WriteAtomically(path, JsonSerializer.Serialize(list, CheckJson.Default.ListString));

    /// <summary>
    /// 一時ファイルに書いて差し替える。差し替えはウイルス対策や索引付けが直前のファイルを
    /// 一瞬掴んでいると失敗することがあるので、少し待って数回やり直す。
    /// </summary>
    private void WriteAtomically(string path, string text)
    {
        System.IO.Directory.CreateDirectory(_dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
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
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "ListString")]
internal partial class CheckJson : JsonSerializerContext { }
