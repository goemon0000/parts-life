using System.Text;

namespace PartsLife.Checks.Recording;

/// <summary>一覧の 1 行（過去の記録を選ぶとき用）。</summary>
public sealed record RunListing(string RunId, string JsonPath, DateTime StartUtc, string Status, int Samples);

/// <summary>
/// 記録の置き場とファイル名。`run-&lt;ID&gt;.json`（記録の見出し）と `sensors-&lt;ID&gt;.csv`（毎秒の値）が対。
/// **ID はファイルから読んだら必ず <see cref="RunId.IsValid"/> を通す**（ファイル名を作るのに使うため）。
/// </summary>
public static class RunStore
{
    /// <summary>ドキュメント\PartsLife\runs。ドキュメントが別の場所に付け替えられていても、その場所に従う。</summary>
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PartsLife", "runs");

    public static string JsonPath(string folder, string id) => Path.Combine(folder, $"run-{id}.json");
    public static string CsvPath(string folder, string id) => Path.Combine(folder, $"sensors-{id}.csv");

    /// <summary>置き場のあるドライブの空き（byte）。分からなければ -1。置き場がまだ無ければ、在る一番近い親で見る。</summary>
    public static long FreeBytes(string folder)
    {
        try
        {
            string? dir = Path.GetFullPath(folder);
            while (dir is not null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir is null) return -1;
            return new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return -1; }
    }

    /// <summary>一時ファイルに書いてから置き換える（途中で落ちても、半分だけ書かれた JSON が残らない）。UTF-8（BOM なし）。</summary>
    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>記録を読み直す。CSV から要約も作り直す。途中で落ちた記録（status が recording のまま）は「interrupted」として扱う。</summary>
    public static (RunSession Session, SeriesSet Series, RunSummary Summary) Load(string jsonPath)
    {
        var session = SessionJson.Read(File.ReadAllText(jsonPath, Encoding.UTF8));
        if (session.Status == "recording")
        {
            session.Status = "interrupted";
            session.StopReason = "app-closed";
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".";
        string csv = CsvPath(dir, session.RunId);
        if (!File.Exists(csv)) throw new FileNotFoundException("センサーの記録（CSV）が見つかりません: " + Path.GetFileName(csv));
        var series = CsvStream.Read(csv);
        if (session.Stats.Samples == 0) session.Stats.Samples = series.Count;
        var summary = Summarizer.Summarize(series, session);
        session.Summary = summary;
        return (session, series, summary);
    }

    /// <summary>置き場の記録を新しい順に（最大 <paramref name="max"/> 件）。読めないファイルは黙って飛ばす。</summary>
    public static IReadOnlyList<RunListing> List(string folder, int max = 50)
    {
        var list = new List<RunListing>();
        if (!Directory.Exists(folder)) return list;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "run-*.json").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var f in files)
        {
            try
            {
                var s = SessionJson.Read(File.ReadAllText(f, Encoding.UTF8));
                list.Add(new RunListing(s.RunId, f, s.StartUtc, s.Status, s.Stats.Samples));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(x => x.StartUtc).Take(max).ToList();
    }
}
