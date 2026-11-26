using System.Text;
using System.Text.Json;

namespace PartsLife.Update;

/// <summary>
/// 最後の版（v0.8.0）の「パーツぐらしは PCミエルの一部になりました」の既読の記録（final-notice.json）。
/// 置き場は <c>%LOCALAPPDATA%\PartsLife\final-notice.json</c>（DESIGN-C 5）。画面を持たないので、決まりを試験で確かめられる。
///
///   - 読めない・壊れている・別の通知 ID・Read が真でない → **既読ではない**（もう一度見せる）
///   - 書くのは「閉じる」か「紹介を開く」を押したとき。同じフォルダの一意な .tmp に書いて Flush(true)、File.Move(overwrite)
///     で入れ替える（途中で落ちても、半端な final-notice.json は残らない）
///   - 書けなかったら偽を返す。呼ぶ側は「記録できませんでした」と出し、既読にしない。失敗したら .tmp は消す
/// </summary>
public sealed class FinalNoticeStore
{
    /// <summary>通知の ID。文面を変えて出し直すときは変える（古い既読が新しい通知を隠さない）。</summary>
    public const string NoticeId = "pcmieru-merge-1";

    public const string FileName = "final-notice.json";

    /// <summary>「PCミエルの紹介を開く」で開く場所。ここ 1 か所だけ。</summary>
    public const string PageUrl = "https://web-autoai.com/pc-mieru";

    /// <summary><c>%LOCALAPPDATA%\PartsLife</c>。</summary>
    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PartsLife");

    public string Dir { get; }

    public string FilePath => Path.Combine(Dir, FileName);

    public FinalNoticeStore(string dir) => Dir = dir;

    /// <summary>この通知を既読にしてあるか。読めなければ（無い・壊れている・別の ID）偽。</summary>
    public bool IsRead()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("NoticeId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == NoticeId
                && root.TryGetProperty("Read", out var read) && read.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>既読と書く。<paramref name="error"/> は書けなかったときの理由（記録には出さない・画面にも出さない。診断用）。</summary>
    /// <returns>書けたか。</returns>
    public bool TryMarkRead(DateTimeOffset now, out string? error)
    {
        string tmp = Path.Combine(Dir, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(Dir);
            string json = "{\"NoticeId\":\"" + NoticeId + "\",\"Read\":true,\"ReadAtUnix\":" + now.ToUnixTimeSeconds() + "}";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            error = e.Message;
            return false;
        }
    }
}
