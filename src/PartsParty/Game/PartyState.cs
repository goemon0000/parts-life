using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartsParty.Game;

/// <summary>
/// 保存される進行。**利用者は一切操作できない。**
/// 機械がどれだけ働いたか、だけで動く。
/// </summary>
public sealed class PartyState
{
    /// <summary>キャラid → 経験値。**その子自身の仕事でしか増えない。**</summary>
    public Dictionary<string, double> Experience { get; set; } = new();

    /// <summary>旅程。物語が進む距離。全体の仕事量から貯まる。</summary>
    public double Journey { get; set; }

    /// <summary>いまの章（0=序章）。</summary>
    public int Chapter { get; set; }

    /// <summary>その章で何場面まで進んだか。</summary>
    public int Scene { get; set; }

    /// <summary>直近に出した文章（画面に残す用）。**新しいものが先頭。**</summary>
    public List<string> Log { get; set; } = new();

    /// <summary>傍白の連発を防ぐため、種類ごとに最後に出した時刻を覚える。</summary>
    public Dictionary<string, long> LastEventAt { get; set; } = new();

    /// <summary>累計の起動時間（秒）。「長い夜」等の判定に使う。</summary>
    public double UptimeSeconds { get; set; }

    /// <summary>前回終了した時刻。数日ぶりの起動を検知する。</summary>
    public long LastSeenUnix { get; set; }

    public int Boots { get; set; }

    /// <summary>表示言語 "ja" / "en"。空なら OS の設定に従う。</summary>
    public string Lang { get; set; } = "";

    /// <summary>窓の大きさ。整数倍でない値も許す（利用者が自由に引っ張れるため）。</summary>
    public double Zoom { get; set; } = 2.0;

    /// <summary>窓の位置。負なら「右上に置く」。</summary>
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;

    public bool Topmost { get; set; } = true;
    public bool Expanded { get; set; }

    // ---------------------------------------------------------------------

    /// <summary>
    /// レベル。**対数で伸ばす。**
    /// 線形にすると、128コアの機械が数時間でカンストして意味が無くなる。
    /// 序盤は数分で上がり、後半は日単位でしか動かない。
    /// </summary>
    public static int LevelOf(double exp) =>
        exp <= 0 ? 1 : (int)Math.Floor(Math.Log(1 + exp / 90.0) / Math.Log(1.55)) + 1;

    public static double ExpForLevel(int level) =>
        level <= 1 ? 0 : 90.0 * (Math.Pow(1.55, level - 1) - 1);

    public int Level(string characterId) => LevelOf(Experience.GetValueOrDefault(characterId));

    /// <summary>次のレベルまでの進み具合 0..1。</summary>
    public double LevelProgress(string characterId)
    {
        double exp = Experience.GetValueOrDefault(characterId);
        int lv = LevelOf(exp);
        double lo = ExpForLevel(lv), hi = ExpForLevel(lv + 1);
        return hi <= lo ? 0 : Math.Clamp((exp - lo) / (hi - lo), 0, 1);
    }

    public void AddExp(string characterId, double amount)
    {
        if (amount <= 0) return;
        Experience[characterId] = Experience.GetValueOrDefault(characterId) + amount;
    }

    // ---------------------------------------------------------------------
    // 保存
    // ---------------------------------------------------------------------
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartsParty");
    private static string FilePath => Path.Combine(Dir, "state.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PartyState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PartyState>(File.ReadAllText(FilePath), Opts) ?? new PartyState();
        }
        catch { /* 壊れていたら作り直す。物語が消えるだけで害はない */ }
        return new PartyState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 書き途中で電源が落ちても壊れないよう、別名で書いてから置き換える
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 保存できなくても動作は続ける */ }
    }
}
