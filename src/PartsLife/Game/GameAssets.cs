using System.IO;
using System.Windows;

namespace PartsLife.Game;

/// <summary>
/// exe に埋め込んだ本文を読む。**WPF の資源に触るのはここだけ。**
/// 決まりの側（PartsLife.Core）は文字列を受け取るだけにしてあり、
/// 画面の無い機械でも試験が回せる。
/// </summary>
public static class GameAssets
{
    public static Story LoadStory() => Story.Parse(ReadResource("story.json"));

    private static string ReadResource(string name)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute);
        using var s = Application.GetResourceStream(uri)?.Stream
                      ?? throw new FileNotFoundException($"{name} が見つかりません");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
