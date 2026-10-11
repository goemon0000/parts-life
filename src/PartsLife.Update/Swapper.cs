namespace PartsLife.Update;

/// <summary>
/// exe の入れ替えと戻し。**動いている exe は消せないが、名前は変えられる**（Windows の決まり）。
///
///   入れ替え: 落とした物を、まず同じフォルダに「名前.new」として写して照合し直す
///             → いまの「名前」を「名前.old」に → 「名前.new」を「名前」に
///             （同じフォルダの中の名前の付け替えだけにする。別のドライブからの移動は途中で切れうるため）
///             途中で 1 つでも失敗したら、それまでに付け替えた分を元に戻す
///   戻し:     「名前」を「名前.failed」に → 「名前.old」を「名前」に
///   後片付け: 「名前.old」「名前.failed」「名前.new」を消す（消せなければ次の起動で）
/// フォルダは引数で受け取る（試験では一時フォルダで回す）。
/// </summary>
public static class Swapper
{
    public const string OldSuffix = ".old";
    public const string NewSuffix = ".new";
    public const string FailedSuffix = ".failed";

    /// <summary>そのフォルダに書けるか（Program Files などは書けない）。試しに小さな物を作って消す。</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".partslife-write-test-" + Environment.ProcessId);
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <returns>入れ替えたら null、できなかったら理由（そのときはフォルダを元のままに戻してある）。</returns>
    public static string? Apply(string appDir, string stagedDir, IReadOnlyList<StagedFile> files)
    {
        if (files.Count == 0) return "入れ替える物が無い";
        var copied = new List<string>();
        try
        {
            // 1. 「名前.new」として写し、照合し直す（置き場から写す間に変わっていないか）
            foreach (var f in files)
            {
                string staged = Path.Combine(stagedDir, f.Name);
                if (!File.Exists(staged)) return $"{f.Name} が置き場に無い";
                if (!Sha256List.Same(Sha256List.HashFile(staged), f.Sha256)) return $"{f.Name} が置き場で変わっている";
                string next = Path.Combine(appDir, f.Name + NewSuffix);
                File.Copy(staged, next, overwrite: true);
                copied.Add(next);
                if (!Sha256List.Same(Sha256List.HashFile(next), f.Sha256)) return $"{f.Name} を写したら中身が変わった";
            }
            // 2. 前回の .old が残っていれば先に消す（消せないなら入れ替えない。.old を上書きして古い版を失わない）
            foreach (var f in files)
            {
                string old = Path.Combine(appDir, f.Name + OldSuffix);
                if (File.Exists(old)) File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var c in copied) TryDelete(c);
            return "入れ替えの用意ができない: " + e.Message;
        }

        // 3. 名前の付け替え。失敗したら付け替えた分を戻す
        var steps = new List<(string Cur, bool MovedOld, bool MovedNew)>();
        try
        {
            foreach (var f in files)
            {
                string cur = Path.Combine(appDir, f.Name);
                steps.Add((cur, false, false));
                if (File.Exists(cur))
                {
                    File.Move(cur, cur + OldSuffix);
                    steps[^1] = (cur, true, false);
                }
                File.Move(cur + NewSuffix, cur);
                steps[^1] = (cur, steps[^1].MovedOld, true);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var (cur, movedOld, movedNew) = steps[i];
                try
                {
                    if (movedNew) File.Delete(cur);                    // 入れた新しい物（まだ動いていない）を外す
                    if (movedOld) File.Move(cur + OldSuffix, cur);     // 元の物を元の名前に
                }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            }
            foreach (var f in files) TryDelete(Path.Combine(appDir, f.Name + NewSuffix));
            return "入れ替えに失敗したので元に戻した: " + e.Message;
        }
    }

    /// <summary>
    /// 新しい版を退けて、.old を元の名前に戻す。新しい版が動いていても（名前を変えるだけなので）できる。
    /// </summary>
    /// <returns>戻せたら null、戻せなかったら理由。</returns>
    public static string? Rollback(string appDir, IReadOnlyList<string> names)
    {
        string? error = null;
        foreach (var name in names)
        {
            string cur = Path.Combine(appDir, name), old = cur + OldSuffix, failed = cur + FailedSuffix;
            if (!File.Exists(old)) continue;
            try
            {
                if (File.Exists(cur))
                {
                    TryDelete(failed);
                    if (File.Exists(failed)) failed += "-" + DateTime.UtcNow.Ticks;
                    File.Move(cur, failed);
                }
                File.Move(old, cur);
                TryDelete(failed);   // 動いていなければ消える。動いていれば後片付けで
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error ??= $"{name} を戻せない: {e.Message}";
            }
        }
        return error;
    }

    /// <summary>.old・.failed・.new の残りを消す。消せた数を返す（動いている物は消えない。次の起動で）。</summary>
    public static int Cleanup(string appDir, IReadOnlyList<string> names)
    {
        int n = 0;
        foreach (var name in names)
        {
            string cur = Path.Combine(appDir, name);
            foreach (var p in new[] { cur + OldSuffix, cur + NewSuffix, cur + FailedSuffix })
                if (File.Exists(p) && TryDelete(p)) n++;
            try
            {
                foreach (var p in Directory.GetFiles(appDir, name + FailedSuffix + "-*"))
                    if (TryDelete(p)) n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>戻せる物（.old）が残っているか。</summary>
    public static bool HasOld(string appDir, IReadOnlyList<string> names) =>
        names.Any(n => File.Exists(Path.Combine(appDir, n + OldSuffix)));

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
