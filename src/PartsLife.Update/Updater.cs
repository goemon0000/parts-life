using System.IO.Compression;

namespace PartsLife.Update;

public enum CheckKind
{
    /// <summary>「確認しない」。何もしていない（通信もしていない）。</summary>
    Off,
    /// <summary>前回から 24 時間経っていない。</summary>
    NotDue,
    /// <summary>回数制限の待ちの間。</summary>
    RateLimited,
    /// <summary>いまの版が最新。</summary>
    UpToDate,
    /// <summary>新しい版がある（知らせるだけ。落としていない）。</summary>
    Available,
    /// <summary>新しい版を落として照合し、入れ替えを待っている。</summary>
    Staged,
    /// <summary>新しい版はあるが、前に入れ替えてうまく起動しなかった版なので入れない。</summary>
    SkippedFailed,
    /// <summary>うまくいかなかった（Message に理由）。</summary>
    Failed,
}

public sealed record CheckResult(CheckKind Kind, string? Version = null, string? PageUrl = null, string? Message = null);

/// <summary>確認と、落として照合するまでに要る物。試験では一時フォルダと偽の通信を渡す。</summary>
public sealed class UpdateContext
{
    /// <summary>exe の置いてあるフォルダ。</summary>
    public required string AppDir { get; init; }
    public required AppVersion Current { get; init; }
    public required UpdateStore Store { get; init; }
    public UpdateEndpoints Endpoints { get; init; } = UpdateEndpoints.GitHub;

    /// <summary>
    /// 通信の口を作る。**「確認しない」のときは呼ばない**（試験で確かめている）。
    /// </summary>
    public Func<HttpMessageHandler> HandlerFactory { get; init; } = UpdateHttp.CreateDefaultHandler;

    /// <summary>いま動いている PartsLife.exe（署名を比べる相手）。</summary>
    public string? RunningExe { get; init; }

    /// <summary>入れ替えられない理由（書けないフォルダ・開発用のビルド）。null なら入れ替えられる。</summary>
    public string? CannotReplaceReason { get; init; }

    public Func<string, SignatureInfo> InspectSignature { get; init; } = DefaultInspect;
    public Func<string, AppVersion?> ReadFileVersion { get; init; } = DefaultFileVersion;
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public InstallLayout Layout => InstallLayouts.Detect(AppDir);

    private static SignatureInfo DefaultInspect(string path) =>
        OperatingSystem.IsWindows() ? Authenticode.Inspect(path) : SignatureInfo.Unsigned;

    private static AppVersion? DefaultFileVersion(string path)
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0) return null;
        return new AppVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }
}

/// <summary>
/// 確認して、（「自動で更新する」なら）落として照合し、置き場に用意するまで。入れ替えはしない（<see cref="Swapper"/>）。
///
/// **照合**（手で落とすときに慎重な人がやることを、全部やる）
///   1. 添付物は、この repo の、その Release の物だけ（<see cref="ReleaseParser"/>）。https で、許した行き先だけ（<see cref="UpdateEndpoints"/>）
///   2. 同じ Release の SHA256.txt を落とし、落とした物の SHA-256 がその名前の行と一致すること。
///      GitHub が添付物に付ける digest があれば、それとも一致すること
///   3. いまの exe が署名つきなら、新しい exe も同じ名乗りの有効な署名であること（<see cref="SignaturePolicy"/>）
///   4. 大きさ（200MB まで・Release の記載どおり）、版（新しい exe に書かれた版が Release の版と同じで、いまより新しい）
/// ZIP の中の exe は、SHA256.txt に行が無い（ZIP 丸ごとの値だけが載っている）。ZIP の値が合えば中身も
/// 1 バイト残らず決まるので、それで足りる。**SHA256.txt の「PartsLife.exe」の行は、単体で配っている exe の値で、
/// ZIP の中の PartsLife.exe（自己圧縮なし）とは別物。** ZIP の中身と比べてはいけない。
/// </summary>
public sealed class Updater
{
    /// <summary>落とす物の大きさの上限。実物は 60MB 台。</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    /// <summary>ZIP から広げる 1 つの大きさの上限。自己圧縮なしの PartsLife.exe は 150MB 前後。</summary>
    public const long MaxExtractedBytes = 400L * 1024 * 1024;

    private readonly UpdateContext _c;

    public Updater(UpdateContext context) => _c = context;

    public async Task<CheckResult> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
    {
        // **「確認しない」なら、ここで終わる。** 通信の口も作らない
        if (mode == UpdateMode.Off) return new CheckResult(CheckKind.Off);

        var now = _c.Time.GetUtcNow();
        var state = _c.Store.LoadState();
        if (UpdateSchedule.InBackoff(state, now))
            return new CheckResult(CheckKind.RateLimited, Message: "GitHub の回数制限のため、しばらく確認しません");
        if (!manual && !UpdateSchedule.IsDue(state, now)) return new CheckResult(CheckKind.NotDue);

        // 行く前に時刻を書く。途中で落ちても、1 日に何度も行かないように
        state.LastCheckUtc = now;
        _c.Store.SaveState(state);

        CheckResult result;
        try
        {
            using var http = new UpdateHttp(_c.HandlerFactory(), _c.Endpoints, _c.Current);
            result = await CheckWithAsync(http, mode, state, ct).ConfigureAwait(false);
        }
        catch (RateLimitedException rl)
        {
            state.BackoffUntilUtc = UpdateSchedule.BackoffUntil(now, rl.RetryAfter, rl.Reset);
            result = new CheckResult(CheckKind.RateLimited, Message: "GitHub の回数制限のため、しばらく確認しません");
        }
        catch (UpdateException e)
        {
            result = new CheckResult(CheckKind.Failed, Message: e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                     or OperationCanceledException or InvalidDataException)
        {
            result = new CheckResult(CheckKind.Failed,
                Message: e is OperationCanceledException ? "時間内に終わらなかった" : e.Message);
        }

        state.LastError = result.Kind == CheckKind.Failed ? result.Message : null;
        _c.Store.SaveState(state);
        return result;
    }

    private async Task<CheckResult> CheckWithAsync(UpdateHttp http, UpdateMode mode, UpdateState state, CancellationToken ct)
    {
        string json = await http.GetLatestReleaseJsonAsync(ct).ConfigureAwait(false);
        var release = ReleaseParser.Parse(json, _c.Endpoints, out var why)
                      ?? throw new UpdateException("最新の Release を読めない: " + why);

        // **いまより新しい版だけ。** 同じ版・古い版（取り下げで最新が戻った等）には何もしない
        if (release.Version <= _c.Current)
        {
            state.LatestVersion = null;
            state.LatestPageUrl = null;
            if (state.Pending is not null) DropPending(state);
            return new CheckResult(CheckKind.UpToDate, release.Version.ToString(), release.PageUrl);
        }
        state.LatestVersion = release.Version.ToString();
        state.LatestPageUrl = release.PageUrl;

        if (state.HasFailed(release.Version))
            return new CheckResult(CheckKind.SkippedFailed, release.Version.ToString(), release.PageUrl,
                                   "前に入れ替えてうまく起動しなかった版なので、入れません");

        if (mode != UpdateMode.Auto || _c.CannotReplaceReason is not null)
            return new CheckResult(CheckKind.Available, release.Version.ToString(), release.PageUrl, _c.CannotReplaceReason);

        // もう用意してあれば落とし直さない（置き場の中身は入れ替える直前に照らし直す）
        if (state.Pending is { } p && p.Version == release.Version.ToString()
            && p.Layout == InstallLayouts.Name(_c.Layout) && PendingIntact(p))
            return new CheckResult(CheckKind.Staged, p.Version, p.PageUrl);

        if (state.Pending is not null) DropPending(state);
        state.Pending = await StageAsync(http, release, ct).ConfigureAwait(false);
        return new CheckResult(CheckKind.Staged, release.Version.ToString(), release.PageUrl);
    }

    /// <summary>置き場の物が、用意したときのまま揃っているか。</summary>
    public bool PendingIntact(PendingUpdate p)
    {
        try
        {
            string dir = _c.Store.FilesDir(p.Version);
            return p.Files.Count > 0 && p.Files.All(f =>
                File.Exists(Path.Combine(dir, f.Name)) && Sha256List.Same(Sha256List.HashFile(Path.Combine(dir, f.Name)), f.Sha256));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void DropPending(UpdateState state)
    {
        if (state.Pending is { } p) _c.Store.DeleteVersionDir(p.Version);
        state.Pending = null;
    }

    private async Task<PendingUpdate> StageAsync(UpdateHttp http, ReleaseInfo release, CancellationToken ct)
    {
        var layout = _c.Layout;
        string assetName = InstallLayouts.AssetFor(layout);
        var asset = release.Find(assetName) ?? throw new UpdateException($"Release に {assetName} が無い");
        var shaAsset = release.Find(InstallLayouts.ShaAsset) ?? throw new UpdateException("Release に SHA256.txt が無い");
        if (asset.Size > MaxDownloadBytes) throw new UpdateException($"{assetName} が大きすぎる");

        var shaText = System.Text.Encoding.UTF8.GetString(
            await http.GetSmallAssetAsync(shaAsset, Sha256List.MaxBytes, ct).ConfigureAwait(false));
        string expected = Sha256List.Parse(shaText).For(assetName)
                          ?? throw new UpdateException($"SHA256.txt に {assetName} の行が無い");
        if (asset.Sha256Digest is { } digest && !Sha256List.Same(digest, expected))
            throw new UpdateException($"SHA256.txt と GitHub の記録で {assetName} の値が違う");

        string version = release.Version.ToString();
        string dir = _c.Store.VersionDir(version), files = _c.Store.FilesDir(version);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(files);

        try
        {
            string part = Path.Combine(dir, assetName + ".part");
            string actual = await http.DownloadAsync(asset, part, MaxDownloadBytes, ct).ConfigureAwait(false);
            if (!Sha256List.Same(actual, expected))
                throw new UpdateException($"{assetName} の SHA-256 が SHA256.txt と一致しない");

            if (layout == InstallLayout.Zip) ExtractZip(part, files);
            else File.Move(part, Path.Combine(files, InstallLayouts.MainExe));
            if (File.Exists(part)) File.Delete(part);

            var pending = new PendingUpdate
            {
                Version = version,
                Layout = InstallLayouts.Name(layout),
                PageUrl = release.PageUrl,
            };
            var running = _c.RunningExe is { } exe && File.Exists(exe) ? _c.InspectSignature(exe) : SignatureInfo.Unsigned;
            foreach (var name in InstallLayouts.FilesFor(layout))
            {
                string path = Path.Combine(files, name);
                if (SignaturePolicy.Check(running, _c.InspectSignature(path), name) is { } bad) throw new UpdateException(bad);
                pending.Files.Add(new StagedFile { Name = name, Sha256 = Sha256List.HashFile(path), Size = new FileInfo(path).Length });
            }

            // 新しい exe に書かれた版が、Release の版と同じで、いまより新しいこと（決して古い版に戻さない）
            var fileVersion = _c.ReadFileVersion(Path.Combine(files, InstallLayouts.MainExe));
            if (fileVersion is not { } fv || fv != release.Version || fv <= _c.Current)
                throw new UpdateException($"新しい exe の版（{fileVersion?.ToString() ?? "読めない"}）が Release の版と合わない");
            return pending;
        }
        catch
        {
            _c.Store.DeleteVersionDir(version);
            throw;
        }
    }

    /// <summary>
    /// ZIP を広げる。**中身は PartsLife.exe と PartsLife.Check.exe の 2 つちょうど**（release.yml の「ZIP に詰める」と同じ検査）。
    /// フォルダ・ほかの物・同じ名前の二重があれば広げない。1 つ <see cref="MaxExtractedBytes"/> まで。
    /// </summary>
    internal static void ExtractZip(string zipPath, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var want = InstallLayouts.FilesFor(InstallLayout.Zip).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!names.SequenceEqual(want, StringComparer.Ordinal))
            throw new UpdateException("ZIP の中身が想定と違う: " + string.Join(", ", names));

        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxExtractedBytes) throw new UpdateException($"ZIP の中の {entry.FullName} が大きすぎる");
            string dest = Path.Combine(destDir, entry.FullName);
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MaxExtractedBytes || total > entry.Length) throw new UpdateException($"ZIP の中の {entry.FullName} が記載より大きい");
                dst.Write(buf, 0, n);
            }
            if (total != entry.Length) throw new UpdateException($"ZIP の中の {entry.FullName} が途中で切れた");
        }
    }
}
