using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartsLife.Update;

/// <summary>
/// 更新の覚え書き。<c>%LOCALAPPDATA%\PartsLife\updates\update-state.json</c>。
/// **進行の保存（%APPDATA%\PartsLife\state.json）とは分けてある。** 更新の都合で進行の保存を書き換えない。
/// 消しても困らない（次の確認が少し早まるだけ）。
/// </summary>
public sealed class UpdateState
{
    /// <summary>最後に確認しに行った時刻（結果に関わらず）。次はこの 24 時間後。</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>回数制限（HTTP 403 / 429）を受けたとき、これより前には行かない。</summary>
    public DateTimeOffset? BackoffUntilUtc { get; set; }

    /// <summary>見つけた新しい版（いまの版より新しい物だけ）と、そのページ。</summary>
    public string? LatestVersion { get; set; }
    public string? LatestPageUrl { get; set; }

    /// <summary>「新しい版があります」を出した版。同じ版で何度も出さない。</summary>
    public string? NotifiedVersion { get; set; }

    /// <summary>入れ替えてうまく起動しなかった版。**同じ版はもう入れない**（もっと新しい版が出たら入れる）。</summary>
    public List<string> FailedVersions { get; set; } = [];

    /// <summary>落として照合まで済み、入れ替えを待っている版。</summary>
    public PendingUpdate? Pending { get; set; }

    /// <summary>元の版に戻したことを、次の起動で一度だけ知らせる（その版）。</summary>
    public string? RolledBackVersion { get; set; }

    /// <summary>最後の確認がうまくいかなかった理由（献立に出す）。うまくいけば消す。</summary>
    public string? LastError { get; set; }

    public bool HasFailed(AppVersion v) => FailedVersions.Contains(v.ToString());

    public void MarkFailed(string version)
    {
        if (!FailedVersions.Contains(version)) FailedVersions.Add(version);
        // 際限なく伸びないように。古い物から落とす（新しい版が出れば古い失敗はもう効かない）
        while (FailedVersions.Count > 20) FailedVersions.RemoveAt(0);
    }
}

public sealed class PendingUpdate
{
    public string Version { get; set; } = "";
    /// <summary>"zip" か "exe"（<see cref="InstallLayouts"/>）。</summary>
    public string Layout { get; set; } = "";
    public string PageUrl { get; set; } = "";
    /// <summary>入れ替える物。名前と、落として照合したときの値（入れ替える直前にもう一度照らす）。</summary>
    public List<StagedFile> Files { get; set; } = [];
}

public sealed class StagedFile
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// 入れ替えの最中の記録。<c>updates\swap.json</c>。
/// 古い版が書いて新しい版を起こし、新しい版が「起動できた」と書く（Confirmed）。
/// 古い版は、それを見るまで隠れて待つ（<see cref="SwapBoot"/>）。
/// </summary>
public sealed class SwapRecord
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public string PageUrl { get; set; } = "";
    /// <summary>入れ替えた古い版のプロセス。新しい版は、これが終わってから .old を消す。</summary>
    public int OldPid { get; set; }
    public DateTimeOffset SwappedAtUtc { get; set; }
    /// <summary>新しい版が、確かめが済む前に起動した回数。3 回目で戻す（落ち続けている）。</summary>
    public int Launches { get; set; }
    /// <summary>新しい版が窓を出して、しばらく無事に動いた。</summary>
    public bool Confirmed { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(SwapRecord))]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal partial class UpdateJson : JsonSerializerContext { }

/// <summary>
/// 覚え書きの読み書き。**読めなければ空から始める**（更新の覚え書きが壊れても、アプリは困らない）。
/// 書くときは別名に書いてから置き換える（書きかけで電源が落ちても、壊れた物を読まない）。
/// </summary>
public sealed class UpdateStore
{
    public string Root { get; }
    public string StatePath => Path.Combine(Root, "update-state.json");
    public string SwapPath => Path.Combine(Root, "swap.json");

    public UpdateStore(string root) => Root = root;

    /// <summary><c>%LOCALAPPDATA%\PartsLife\updates</c>。</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PartsLife", "updates");

    /// <summary>その版を落として広げる場所。<c>updates\&lt;版&gt;\</c>。</summary>
    public string VersionDir(string version) => Path.Combine(Root, version);
    public string FilesDir(string version) => Path.Combine(VersionDir(version), "files");

    public UpdateState LoadState() =>
        Read(StatePath, UpdateJson.Default.UpdateState) ?? new UpdateState();

    public void SaveState(UpdateState s) =>
        Write(StatePath, JsonSerializer.Serialize(s, UpdateJson.Default.UpdateState));

    public SwapRecord? LoadSwap() => Read(SwapPath, UpdateJson.Default.SwapRecord);

    public void SaveSwap(SwapRecord r) =>
        Write(SwapPath, JsonSerializer.Serialize(r, UpdateJson.Default.SwapRecord));

    public void DeleteSwap()
    {
        try { File.Delete(SwapPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>この版以下（もう要らない版）の置き場を消す。消せなければ次の機会に。</summary>
    public void DeleteStagedUpTo(AppVersion keepAbove)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var d in Directory.GetDirectories(Root))
            {
                if (!AppVersion.TryParse(Path.GetFileName(d), out var v) || v > keepAbove) continue;
                try { Directory.Delete(d, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void DeleteVersionDir(string version)
    {
        try
        {
            string d = VersionDir(version);
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void Write(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Root);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 書けなくても本体は動く */ }
    }
}
