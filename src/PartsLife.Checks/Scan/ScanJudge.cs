namespace PartsLife.Checks.Scan;

/// <summary>表面検査の結果の判定。**純粋な関数。**</summary>
public static class ScanJudge
{
    public static IReadOnlyList<Finding> Judge(ScanResult r)
    {
        var f = new List<Finding>();
        if (r.Blocks == 0)
        {
            f.Add(new Finding(L.T("表面検査", "Surface scan"), Verdict.Unavailable,
                r.AbortReason ?? (r.Stopped ? L.T("始める前に止めました", "stopped before starting") : L.T("何も読めませんでした", "nothing was read"))));
            return f;
        }

        string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";
        if (r.ErrorCount > 0)
        {
            string where = string.Join(", ", r.Errors.Take(5).Select(e => Gb(e.Offset)));
            f.Add(new Finding(L.T("読めない所", "Unreadable blocks"), Verdict.Check,
                L.T($"{L.N(r.ErrorCount)} か所（1 MiB 単位、位置の例: {where}）。大事なデータのバックアップを",
                    $"{L.N(r.ErrorCount)} block(s) of 1 MiB (e.g. at {where}). Back up important data")));
        }
        else
            f.Add(new Finding(L.T("読めない所", "Unreadable blocks"), Verdict.Ok, L.T("ありません", "none")));

        if (r.SlowCount > 0)
            f.Add(new Finding(L.T("遅い所", "Slow blocks"), r.SlowCount >= 50 ? Verdict.Check : Verdict.Caution,
                L.T($"{L.N(r.SlowCount)} か所（普段 {r.MedianMs:0.0} ms のところ、最大 {r.MaxMs:0} ms）。読み直しに手間取っている場所の可能性",
                    $"{L.N(r.SlowCount)} block(s) (typical {r.MedianMs:0.0} ms, worst {r.MaxMs:0} ms) — the drive may be retrying there")));
        else
            f.Add(new Finding(L.T("遅い所", "Slow blocks"), Verdict.Ok,
                L.T($"ありません（普段 {r.MedianMs:0.0} ms、最大 {r.MaxMs:0} ms）", $"none (typical {r.MedianMs:0.0} ms, worst {r.MaxMs:0} ms)")));

        double share = r.DiskBytes <= 0 ? 0 : (double)r.ReadBytes / r.DiskBytes * 100;
        string scope = r.Full
            ? L.T($"全域 {Gb(r.DiskBytes)} のうち {Gb(r.ReadBytes)} を読みました", $"read {Gb(r.ReadBytes)} of {Gb(r.DiskBytes)} (full)")
            : L.T($"{r.Every} ブロックに 1 つの間引きで、全体の {share:0.0}%（{Gb(r.ReadBytes)}）だけ読みました。読んでいない所の状態は分かりません",
                  $"sampled 1 in {r.Every} blocks: {share:0.0}% of the disk ({Gb(r.ReadBytes)}). Unread areas are unknown");
        f.Add(new Finding(L.T("範囲", "Coverage"), Verdict.Ok,
            scope + L.T($"（{r.Seconds:0} 秒、平均 {r.AverageMBps:0} MB/s）", $" ({r.Seconds:0} s, {r.AverageMBps:0} MB/s on average)")));

        if (r.Stopped)
            f.Add(new Finding(L.T("中断", "Stopped"), Verdict.Caution, L.T("途中で止めました。ここまでの結果です", "stopped early; partial results")));
        else if (r.AbortReason is { } why)
            f.Add(new Finding(L.T("中断", "Stopped"), Verdict.Check, why));
        return f;
    }
}
