using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PartsLife.Checks.Build;

/// <summary>構成コードを作った結果。</summary>
/// <param name="Code">`PCB1.` ＋ JSON の base64url。クリップボードへ写す物。</param>
/// <param name="Json">同じ中身を読める形に整えた JSON（ファイルに保存する物・「中身を見る」で見せる物）。</param>
/// <param name="Config">実際にコードに入れた部品（長さの上限で省いた後）。</param>
/// <param name="Notes">省いた物の説明（画面に出す）。</param>
public sealed record BuildCodeResult(string Code, string Json, BuildConfig Config, IReadOnlyList<string> Notes);

/// <summary>
/// 構成コード（web-autoai.com の /my-config に貼って「マイPC」に登録する文字列）。
///
/// 形: `PCB1.` ＋ UTF-8 の JSON を base64url（パディングなし、+ → -、/ → _）にした物。
/// **受け手と同時に作っている約束なので、形を変えるときは v を上げる。**
/// <code>{"v":1,"src":"parts-life","p":{"cpu":[{"n":"…"}],"ram":[{"n":"…","cap":16,"spd":3200,"t":"DDR4","mfr":"…","q":2}]}}</code>
///
/// **このアプリは送信しない。** 文字列を作るだけで、写すか保存するかは使う人が決める。
/// 入れるのは型番と数字だけ（シリアル番号・PC の名前・利用者名・ドライブ文字は持つ場所が無い）。
/// JSON は手で書く（Utf8JsonWriter）。このアセンブリは NativeAOT の補助 exe にも入るので、リフレクション頼みの直列化は使わない。
/// </summary>
public static class BuildCode
{
    public const string Prefix = "PCB1.";
    public const int Version = 1;
    public const string Source = "parts-life";
    /// <summary>接頭辞の後ろの文字数の上限。</summary>
    public const int MaxLength = 6000;
    public const int MaxPerCategory = 8;
    public const int MaxQuantity = 16;

    /// <summary>
    /// 長さの上限を超えたときに省く順。**機械の性能に効かない物から。**
    /// モニター → HDD → メモリの2種類目以降（同じ物は q でまとめた後）。光学ドライブはモニターより先。
    /// </summary>
    internal static readonly string[] DropFirst = { "optical-drive", "monitor", "hdd" };

    /// <summary>部品の一覧から、コード・JSON・省いた物の説明を作る。</summary>
    public static BuildCodeResult Make(BuildConfig raw)
    {
        var notes = new List<string>();
        var c = Normalize(raw, notes);
        string code = Encode(c);
        if (code.Length - Prefix.Length > MaxLength) code = Trim(c, notes);
        return new BuildCodeResult(code, ToJson(c, indented: true), c, notes);
    }

    /// <summary>
    /// 名前を整え、シリアルに見える物を落とし、同じ部品を q でまとめ、1 分類 8 種類までにする。
    /// </summary>
    public static BuildConfig Normalize(BuildConfig raw, List<string>? notes = null)
    {
        var c = new BuildConfig();
        foreach (var cat in BuildConfig.Categories)
        {
            var parts = new List<BuildPart>();
            foreach (var p in raw[cat])
            {
                var s = Sanitize(p);
                if (s is null) continue;
                int i = parts.FindIndex(x => x.Key == s.Key);
                if (i >= 0) parts[i] = parts[i] with { Q = Math.Min(MaxQuantity, parts[i].Q + s.Q) };
                else parts.Add(s with { Q = Math.Clamp(s.Q, 1, MaxQuantity) });
            }
            if (parts.Count > MaxPerCategory)
            {
                notes?.Add(L.T($"{CategoryName(cat)}は {MaxPerCategory} 種類までなので、残りの {parts.Count - MaxPerCategory} 種類を省きました",
                               $"{CategoryName(cat)}: only {MaxPerCategory} kinds fit, so {parts.Count - MaxPerCategory} were left out"));
                parts = parts.Take(MaxPerCategory).ToList();
            }
            if (parts.Count > 0) c.Set(cat, parts);
        }
        return c;
    }

    /// <summary>1つの部品を約束の範囲に収める。名前が使えない（空・シリアルに見える）なら null。</summary>
    internal static BuildPart? Sanitize(BuildPart p)
    {
        string n = BuildRules.CleanName(p.Name);
        if (BuildRules.IsPlaceholder(n) || BuildRules.LooksLikeSerial(n)) return null;
        string? t = Opt(p.Type, 8);
        string? m = Opt(p.Mfr, 40);
        if (m is not null && BuildRules.LooksLikeSerial(m)) m = null;
        return new BuildPart(n, Pos(p.Cap), Pos(p.Speed), t, m, Pos(p.Vram), p.Q);

        static string? Opt(string? s, int max)
        {
            string v = BuildRules.CleanName(s, max);
            return v.Length == 0 ? null : v;
        }
        static int? Pos(int? v) => v is > 0 ? v : null;
    }

    /// <summary>上限に収まるまで、決めた順に省く。</summary>
    private static string Trim(BuildConfig c, List<string> notes)
    {
        bool Fits(out string code)
        {
            code = Encode(c);
            return code.Length - Prefix.Length <= MaxLength;
        }

        string code;
        foreach (var cat in DropFirst)
        {
            if (c[cat].Count == 0) continue;
            c.Remove(cat);
            notes.Add(L.T($"長さの上限（{MaxLength:N0} 字）に収めるため、{CategoryName(cat)}を省きました",
                          $"Left out {CategoryName(cat)} to stay within {MaxLength:N0} characters"));
            if (Fits(out code)) return code;
        }
        if (c["ram"].Count > 1)
        {
            c.Set("ram", c["ram"].Take(1));
            notes.Add(L.T($"長さの上限（{MaxLength:N0} 字）に収めるため、メモリは1種類目だけにしました",
                          $"Kept only the first kind of memory to stay within {MaxLength:N0} characters"));
            if (Fits(out code)) return code;
        }
        // ここまで来ることは普通ない（8 種類 × 80 字でも収まる）。来たら、後ろの分類から1つずつ削る
        string[] order = { "ssd", "gpu", "ram", "motherboard", "cpu" };
        while (!Fits(out code))
        {
            var cat = order.FirstOrDefault(x => c[x].Count > 1) ?? order.FirstOrDefault(x => c[x].Count > 0);
            if (cat is null) break;
            c.Set(cat, c[cat].Take(c[cat].Count - 1));
            if (c[cat].Count == 0) c.Remove(cat);
            notes.Add(L.T($"長さの上限に収めるため、{CategoryName(cat)}を1つ省きました", $"Left out one {CategoryName(cat)} to fit"));
        }
        return code;
    }

    /// <summary>`PCB1.` ＋ 詰めた JSON の base64url。省きはしない（<see cref="Make"/> を使う）。</summary>
    public static string Encode(BuildConfig c) =>
        Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ToJson(c, indented: false)));

    /// <summary>コードから JSON を取り出す（試験と、貼られたコードの確認用）。形が違えば FormatException。</summary>
    public static string Decode(string code)
    {
        string s = code.Trim();
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("PCB1. で始まっていません");
        try { return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(s.AsSpan(Prefix.Length))); }
        catch (FormatException) { throw; }
        catch (Exception ex) { throw new FormatException(ex.Message, ex); }
    }

    /// <summary>
    /// JSON にする。空の分類は書かない。部品の欄は n・cap・spd・t・mfr・vram・q の順（無い物は書かない）。
    /// 和文の名前もそのまま読めるように、\uXXXX には逃がさない（JSON として正しいことは変わらない）。
    /// </summary>
    public static string ToJson(BuildConfig c, bool indented)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        var opts = new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        using (var w = new Utf8JsonWriter(buf, opts))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("src", Source);
            w.WriteStartObject("p");
            foreach (var cat in c.NonEmpty)
            {
                w.WriteStartArray(cat);
                foreach (var p in c[cat])
                {
                    w.WriteStartObject();
                    w.WriteString("n", p.Name);
                    if (p.Cap is { } cap) w.WriteNumber("cap", cap);
                    if (p.Speed is { } spd) w.WriteNumber("spd", spd);
                    if (p.Type is { } t) w.WriteString("t", t);
                    if (p.Mfr is { } m) w.WriteString("mfr", m);
                    if (p.Vram is { } vram) w.WriteNumber("vram", vram);
                    if (p.Q > 1) w.WriteNumber("q", p.Q);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>分類の呼び名（画面の文言用）。</summary>
    public static string CategoryName(string cat) => cat switch
    {
        "cpu" => "CPU",
        "gpu" => "GPU",
        "motherboard" => L.T("マザーボード", "motherboard"),
        "ram" => L.T("メモリ", "memory"),
        "ssd" => "SSD",
        "hdd" => "HDD",
        "monitor" => L.T("モニター", "monitor"),
        "optical-drive" => L.T("光学ドライブ", "optical drive"),
        _ => cat,
    };
}
