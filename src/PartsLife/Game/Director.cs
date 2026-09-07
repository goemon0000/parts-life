using PartsLife.Sensors;

namespace PartsLife.Game;

/// <summary>実測から作った、1秒ぶんの「働き」。</summary>
public sealed record Work(
    double Cpu,          // 0..1
    double Gpu,          // 0..1（最も忙しいGPU）
    double Memory,       // 0..1
    double Disk,         // 0..1 に丸めた読み書き
    double DiskFullest); // 0..1 一番埋まっているドライブ

/// <summary>
/// 実機の稼働を、経験値と物語に変える係。
///
/// **設計の要点は「働いた分だけ進む」を利用者が体感できること。**
/// cpu-z で負荷をかけたのに何も動かない、では仕掛けが無いのと同じになる。
/// そのため負荷時と待機時で、進む速さに 5倍以上の差を付けてある。
///
/// 経験値は **その子自身の仕事でしか増えない。**
/// CPU を回しても GPU は上がらない。そこが崩れると、ただの時間経過になる。
/// </summary>
public sealed class Director
{
    private readonly Story _story;
    private readonly PartyState _state;

    /// <summary>待機が続いた秒数。焚き火の場面を出す判定に使う。</summary>
    private double _idleSeconds;
    private bool _bootDone;
    private readonly Random _rnd = new();

    public Director(Story story, PartyState state)
    {
        _story = story;
        _state = state;
    }

    public PartyState State => _state;
    public Story Story => _story;

    /// <summary>いまの章。</summary>
    public Chapter CurrentChapter => _story.Chapters[Math.Clamp(_state.Chapter, 0, _story.Chapters.Count - 1)];

    /// <summary>直近に出した文章（新しいものが先頭）。</summary>
    public IReadOnlyList<string> Recent => _state.Log;

    // ---------------------------------------------------------------------
    // 経験値
    // ---------------------------------------------------------------------

    /// <summary>1秒あたりの取得経験値。待機でも僅かに入る（点けているだけでも進む）。</summary>
    private static double ExpRate(double load) => 0.6 + Math.Clamp(load, 0, 1) * 11.0;

    // ---------------------------------------------------------------------
    // 進行
    // ---------------------------------------------------------------------

    /// <summary>
    /// dt 秒ぶん進める。呼ぶのは1秒に1回で十分。
    /// 返り値は「今回新しく出た文章」（無ければ空）。
    /// </summary>
    public IReadOnlyList<string> Tick(double dt, Work w, IReadOnlyList<PartySlot> slots, string lang)
    {
        var produced = new List<string>();
        _state.UptimeSeconds += dt;

        // --- 経験値 ---
        // **その子自身の仕事でしか増えない。**
        // GPU が2枚なら、回っている方だけが育つ。ここが崩れると、ただの時間経過になる。
        foreach (var slot in slots)
        {
            int before = _state.Level(slot.Key);
            _state.AddExp(slot.Key, ExpRate(slot.Load) * dt);
            int after = _state.Level(slot.Key);
            if (after > before)
            {
                var t = _story.EventById("level-up");
                if (t is { Texts.Count: > 0 })
                    produced.Add(Pick(t).For(lang)
                        .Replace("{name}", DisplayName(slot.CharacterId, lang)) + $"  (Lv.{after})");
            }
        }

        // --- 旅程 ---
        // 待機 0.24/秒、全力 1.4/秒 前後。序章の1場面が 14 なので、
        // 負荷をかければ十数秒で文章が動く。放っておいても数分で動く。
        double effort = w.Cpu + w.Gpu + w.Memory * 0.3 + w.Disk * 0.5;
        _state.Journey += (0.15 + effort * 0.62) * dt;

        // --- 章と場面 ---
        int chapter = _story.ChapterAt(_state.Journey);
        if (chapter != _state.Chapter)
        {
            _state.Chapter = chapter;
            _state.Scene = 0;
            produced.Add("── " + CurrentChapter.Title.For(lang) + " ──");
        }

        var ch = CurrentChapter;
        int wantScene = ch.ScenePer <= 0 ? 0 : (int)((_state.Journey - ch.Require) / ch.ScenePer);
        wantScene = Math.Min(wantScene, ch.Scenes.Count - 1);
        while (_state.Scene < wantScene)
        {
            _state.Scene++;
            produced.Add(ch.Scenes[_state.Scene].For(lang));
        }
        // 章の頭の一文は、まだ何も出していなければ出す
        if (_state.Log.Count == 0 && ch.Scenes.Count > 0)
            produced.Add(ch.Scenes[Math.Clamp(_state.Scene, 0, ch.Scenes.Count - 1)].For(lang));

        // --- 傍白 ---
        foreach (var text in CheckEvents(dt, w, lang))
            produced.Add(text);

        foreach (var line in produced)
        {
            _state.Log.Insert(0, line);
            if (_state.Log.Count > 40) _state.Log.RemoveRange(40, _state.Log.Count - 40);
        }
        return produced;
    }

    private IEnumerable<string> CheckEvents(double dt, Work w, string lang)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!_bootDone)
        {
            _bootDone = true;
            long gap = _state.LastSeenUnix == 0 ? 0 : now - _state.LastSeenUnix;
            string kind = gap > 3 * 24 * 3600 ? "long-absence" : "boot";
            if (TryFire(kind, now, lang, out var boot)) yield return boot;
        }

        _idleSeconds = w.Cpu <= 0.05 ? _idleSeconds + dt : 0;

        foreach (var kind in Conditions(w))
            if (TryFire(kind, now, lang, out var text))
                yield return text;
    }

    private IEnumerable<string> Conditions(Work w)
    {
        if (w.Cpu >= 0.90) yield return "cpu-very-high";
        if (w.Gpu >= 0.85) yield return "gpu-very-high";
        if (w.Memory >= 0.90) yield return "memory-tight";
        if (w.Disk >= 0.60) yield return "disk-busy";
        if (w.DiskFullest >= 0.90) yield return "disk-full";
        if (_idleSeconds >= 120) yield return "idle-long";
    }

    private bool TryFire(string when, long now, string lang, out string text)
    {
        text = "";
        var candidates = _story.EventsFor(when).ToList();
        if (candidates.Count == 0) return false;
        var ev = candidates[_rnd.Next(candidates.Count)];

        if (_state.LastEventAt.TryGetValue(ev.When, out long last) && now - last < ev.Cooldown)
            return false;
        if (ev.Texts.Count == 0) return false;

        _state.LastEventAt[ev.When] = now;
        text = Pick(ev).For(lang);
        return true;
    }

    private LocalizedText Pick(StoryEvent ev) => ev.Texts[_rnd.Next(ev.Texts.Count)];

    /// <summary>物語の中で呼ぶ名。アトラス側の name とは別に持つ（文章の据わりが違うため）。</summary>
    public static string DisplayName(string id, string lang) => lang == "en"
        ? id switch
        {
            "cpu" => "The CPU", "cpu-cooler" => "The cooler", "memory" => "The memory",
            "gpu" => "The GPU", "ssd" => "The SSD", "hdd" => "The HDD",
            "psu" => "The PSU", "motherboard" => "The motherboard", _ => id,
        }
        : id switch
        {
            "cpu" => "CPUくん", "cpu-cooler" => "クーラーくん", "memory" => "メモリくん",
            "gpu" => "GPUくん", "ssd" => "SSDくん", "hdd" => "HDDくん",
            "psu" => "電源くん", "motherboard" => "マザーボードくん", _ => id,
        };
}
