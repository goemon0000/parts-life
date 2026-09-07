using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PartsParty.Game;

/// <summary>
/// 物語の本文。**中身は assets/story.json にあり、ここには一文も書かない。**
/// 場面を足すのに C# の再ビルドを要らなくするため。
/// 条件（when）だけは名前で決め打ちにしてある。式を解釈する仕組みを入れると、
/// 「文章を足したいだけ」の作業に言語処理系の面倒が付いてくるため。
/// </summary>
public sealed class Story
{
    public IReadOnlyList<Chapter> Chapters { get; }
    public IReadOnlyList<StoryEvent> Events { get; }

    private Story(StoryFile f)
    {
        Chapters = f.Chapters;
        Events = f.Events;
    }

    public static Story Load()
    {
        var uri = new Uri("pack://application:,,,/Assets/story.json", UriKind.Absolute);
        using var s = Application.GetResourceStream(uri)?.Stream
                      ?? throw new FileNotFoundException("story.json が見つかりません");
        using var r = new StreamReader(s);
        var file = JsonSerializer.Deserialize<StoryFile>(r.ReadToEnd(),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("story.json を読めませんでした");
        if (file.Version != 1)
            throw new InvalidDataException($"story.json の形式が想定と違います (version={file.Version})");
        return new Story(file);
    }

    /// <summary>旅程から、いまいるべき章の番号。</summary>
    public int ChapterAt(double journey)
    {
        int idx = 0;
        for (int i = 0; i < Chapters.Count; i++)
            if (journey >= Chapters[i].Require) idx = i;
        return idx;
    }

    public StoryEvent? EventById(string id) => Events.FirstOrDefault(e => e.Id == id);
    public IEnumerable<StoryEvent> EventsFor(string when) => Events.Where(e => e.When == when);

    // -- json の形 ------------------------------------------------------------
    private sealed class StoryFile
    {
        public int Version { get; set; }
        public List<Chapter> Chapters { get; set; } = new();
        public List<StoryEvent> Events { get; set; } = new();
    }
}

public sealed class Chapter
{
    public string Id { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    /// <summary>この章に入るのに必要な旅程。</summary>
    public double Require { get; set; }
    /// <summary>次の場面へ進むのに要る旅程。</summary>
    public double ScenePer { get; set; } = 20;
    public List<LocalizedText> Scenes { get; set; } = new();
}

public sealed class StoryEvent
{
    public string Id { get; set; } = "";
    /// <summary>条件の名前。Director が解釈する。</summary>
    public string When { get; set; } = "";
    /// <summary>同じ種類を再び出せるまでの秒数。</summary>
    public double Cooldown { get; set; }
    public List<LocalizedText> Texts { get; set; } = new();
}

public sealed class LocalizedText
{
    public string Ja { get; set; } = "";
    public string En { get; set; } = "";
    public string For(string lang) => lang == "en" ? (En.Length > 0 ? En : Ja) : (Ja.Length > 0 ? Ja : En);
}
