namespace PartsLife.Update;

/// <summary>
/// ファイルの操作。試験では、決めた場所で失敗させる物に差し替える（<see cref="Swapper"/> の失敗の道を確かめるため）。
/// </summary>
public class FileOps
{
    public static readonly FileOps Real = new();
    public virtual void Move(string from, string to) => File.Move(from, to);
    public virtual void Delete(string path) => File.Delete(path);
    public virtual void Copy(string from, string to) => File.Copy(from, to, overwrite: true);
    public virtual bool Exists(string path) => File.Exists(path);
}

/// <summary>
/// 入れ替えの結果。
///   Ok    … 入れ替えた
///   Dirty … 入れ替えられず、しかも元にも戻し切れなかった（新しい物が入ったまま・.old が残っている）。
///           swap.json を残して、次の起動で戻し直す（<see cref="SwapBoot"/> の Rollback）
///   どちらでもない … 入れ替えられなかったが、フォルダは元のまま
/// </summary>
public readonly record struct SwapOutcome(bool Ok, bool Dirty, string? Error)
{
    public static readonly SwapOutcome Success = new(true, false, null);
    public static SwapOutcome Clean(string error) => new(false, false, error);
    public static SwapOutcome Unclean(string error) => new(false, true, error);
}

/// <summary>
/// exe の入れ替えと戻し。**動いている exe は消せないが、名前は変えられる**（Windows の決まり）。
///
///   入れ替え: 落とした物を、まず同じフォルダに「名前.new」として写して照合し直す
///             → いまの「名前」を「名前.old」に → 「名前.new」を「名前」に
///             （同じフォルダの中の名前の付け替えだけにする。別のドライブからの移動は途中で切れうるため）
///             途中で 1 つでも失敗したら、それまでに付け替えた分を元に戻す
///   戻し:     「名前」を「名前.failed」に → 「名前.old」を「名前」に。
///             後ろが失敗したら「名前.failed」を「名前」に戻す（**PartsLife.exe が無い状態では終わらない**）
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

    public static SwapOutcome Apply(string appDir, string stagedDir, IReadOnlyList<StagedFile> files, FileOps? ops = null)
    {
        ops ??= FileOps.Real;
        if (files.Count == 0) return SwapOutcome.Clean("入れ替える物が無い");
        var copied = new List<string>();
        try
        {
            // 1. 「名前.new」として写し、照合し直す（置き場から写す間に変わっていないか）
            foreach (var f in files)
            {
                string staged = Path.Combine(stagedDir, f.Name);
                if (!ops.Exists(staged)) return Discard(ops, copied, $"{f.Name} が置き場に無い");
                if (!Sha256List.Same(Sha256List.HashFile(staged), f.Sha256)) return Discard(ops, copied, $"{f.Name} が置き場で変わっている");
                string next = Path.Combine(appDir, f.Name + NewSuffix);
                ops.Copy(staged, next);
                copied.Add(next);
                if (!Sha256List.Same(Sha256List.HashFile(next), f.Sha256)) return Discard(ops, copied, $"{f.Name} を写したら中身が変わった");
            }
            // 2. 前回の .old が残っていれば先に消す（消せないなら入れ替えない。.old を上書きして古い版を失わない）
            foreach (var f in files)
            {
                string old = Path.Combine(appDir, f.Name + OldSuffix);
                if (ops.Exists(old)) ops.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Discard(ops, copied, "入れ替えの用意ができない: " + e.Message);
        }

        // 3. 名前の付け替え。失敗したら付け替えた分を戻す
        var steps = new List<(string Cur, bool MovedOld, bool MovedNew)>();
        try
        {
            foreach (var f in files)
            {
                string cur = Path.Combine(appDir, f.Name);
                steps.Add((cur, false, false));
                if (ops.Exists(cur))
                {
                    ops.Move(cur, cur + OldSuffix);
                    steps[^1] = (cur, true, false);
                }
                ops.Move(cur + NewSuffix, cur);
                steps[^1] = (cur, steps[^1].MovedOld, true);
            }
            return SwapOutcome.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            string? stuck = null;
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var (cur, movedOld, movedNew) = steps[i];
                string? err = movedNew ? RestoreOne(cur, movedOld, ops)
                            : movedOld ? PutBack(cur, ops)
                            : null;
                stuck ??= err;
            }
            foreach (var f in files) TryDelete(ops, Path.Combine(appDir, f.Name + NewSuffix));
            return stuck is null
                ? SwapOutcome.Clean("入れ替えに失敗したので元に戻した: " + e.Message)
                : SwapOutcome.Unclean($"入れ替えに失敗し、元にも戻し切れなかった（次の起動で戻し直す）: {e.Message} / {stuck}");
        }
    }

    /// <summary>
    /// 新しい版を退けて、.old を元の名前に戻す。新しい版が動いていても（名前を変えるだけなので）できる。
    /// </summary>
    /// <returns>戻せたら null、戻せなかったら理由（.old は残してある。次の起動で戻し直す）。</returns>
    public static string? Rollback(string appDir, IReadOnlyList<string> names, FileOps? ops = null)
    {
        ops ??= FileOps.Real;
        string? error = null;
        foreach (var name in names)
        {
            string cur = Path.Combine(appDir, name);
            if (!ops.Exists(cur + OldSuffix)) continue;
            // 1 つ戻せなくても、残りは戻す（??= だと右が呼ばれない）
            string? err = RestoreOne(cur, hadOld: true, ops);
            error ??= err;
        }
        return error;
    }

    /// <summary>
    /// 1 つを元に戻す。「名前」→「名前.failed」→「名前.old」→「名前」。
    /// 後ろが失敗したら「名前.failed」を「名前」に戻す（写しでもよい）。**「名前」が無いまま終わらない。**
    /// hadOld が偽（元々その物が無かった）なら、入れた新しい物を消すだけ。
    /// </summary>
    private static string? RestoreOne(string cur, bool hadOld, FileOps ops)
    {
        string old = cur + OldSuffix;
        if (!hadOld)
        {
            try { if (ops.Exists(cur)) ops.Delete(cur); return null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(cur)} を外せない: {e.Message}"; }
        }
        if (!ops.Exists(old)) return null;

        string failed = cur + FailedSuffix;
        bool movedAway = false;
        try
        {
            if (ops.Exists(cur))
            {
                TryDelete(ops, failed);
                if (ops.Exists(failed)) failed += "-" + DateTime.UtcNow.Ticks;
                ops.Move(cur, failed);
                movedAway = true;
            }
            ops.Move(old, cur);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (movedAway && !ops.Exists(cur))
            {
                // 退けた新しい物を元の名前に戻す。それもだめなら写す。それもだめなら古い物を写す
                try { ops.Move(failed, cur); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
                {
                    try { ops.Copy(failed, cur); }
                    catch (Exception e3) when (e3 is IOException or UnauthorizedAccessException)
                    {
                        try { ops.Copy(old, cur); } catch (Exception e4) when (e4 is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            return $"{Path.GetFileName(cur)} を戻せない: {e.Message}";
        }
        TryDelete(ops, failed);   // 動いていなければ消える。動いていれば後片付けで
        return null;
    }

    /// <summary>「名前」を「名前.old」にした直後に失敗した（新しい物はまだ入っていない）。.old を元の名前に戻す。</summary>
    private static string? PutBack(string cur, FileOps ops)
    {
        try { ops.Move(cur + OldSuffix, cur); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { ops.Copy(cur + OldSuffix, cur); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            return $"{Path.GetFileName(cur)} を元の名前に戻せない: {e.Message}";
        }
    }

    private static SwapOutcome Discard(FileOps ops, List<string> copied, string error)
    {
        foreach (var c in copied) TryDelete(ops, c);
        return SwapOutcome.Clean(error);
    }

    /// <summary>.old・.failed・.new の残りを消す。消せた数を返す（動いている物は消えない。次の起動で）。</summary>
    public static int Cleanup(string appDir, IReadOnlyList<string> names)
    {
        int n = 0;
        foreach (var name in names)
        {
            string cur = Path.Combine(appDir, name);
            foreach (var p in new[] { cur + OldSuffix, cur + NewSuffix, cur + FailedSuffix })
                if (File.Exists(p) && TryDelete(FileOps.Real, p)) n++;
            try
            {
                foreach (var p in Directory.GetFiles(appDir, name + FailedSuffix + "-*"))
                    if (TryDelete(FileOps.Real, p)) n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>戻せる物（.old）が残っているか。</summary>
    public static bool HasOld(string appDir, IReadOnlyList<string> names) =>
        names.Any(n => File.Exists(Path.Combine(appDir, n + OldSuffix)));

    private static bool TryDelete(FileOps ops, string path)
    {
        try
        {
            if (ops.Exists(path)) ops.Delete(path);
            return !ops.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
