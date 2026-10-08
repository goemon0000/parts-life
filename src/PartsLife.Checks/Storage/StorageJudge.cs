namespace PartsLife.Checks.Storage;

public sealed record DiskJudgement(DiskReport Report, IReadOnlyList<Finding> Findings,
                                   IReadOnlyList<CounterDelta> Deltas, BaselineEntry? Previous,
                                   BaselineState Baseline = BaselineState.First)
{
    /// <summary>
    /// 一番重い判定。**測れなかった物は重さに数えない**
    /// （ただし全部測れなかったら「測れず」）。
    /// </summary>
    public Verdict Overall
    {
        get
        {
            var measured = Findings.Where(f => f.Verdict != Verdict.Unavailable).ToList();
            return measured.Count == 0 ? Verdict.Unavailable : measured.Max(f => f.Verdict);
        }
    }
}

/// <summary>
/// 記憶媒体の読み取り結果を判定する。**純粋な関数。** 読むのは補助 exe、記録は BaselineStore。
///
/// ■ 方針
/// - 累計の大きさより、**前回からの増え方**を重く見る
/// - 機器自身が閾値で「故障の見込み」と言っているなら、それをそのまま伝える
/// - 読めなかった物は「測れず」。OK にしない
/// - 前回の記録と比べられなかった（識別子が無い・同じ識別子の台が複数）ときは、**増えたとは言わない**
/// </summary>
public static class StorageJudge
{
    /// <param name="state">基準値の状態（<see cref="BaselineStore.RecordAll"/> の結果）。
    /// 省略すると、前回の記録があれば「比べた」、無ければ「初回」。</param>
    public static DiskJudgement Judge(DiskReport r, BaselineEntry? previous, BaselineState? state = null)
    {
        var st = state ?? (previous is not null ? BaselineState.Compared : BaselineState.First);
        // 比べたと言えるときだけ前回の値を使う
        if (st != BaselineState.Compared) previous = null;
        var current = Counters.From(r);
        var deltas = BaselineStore.Diff(previous, current);
        var findings = new List<Finding>();
        string? noCompare = BaselineStates.WhyNotCompared(st);

        if (r.Unavailable is { } why)
        {
            findings.Add(new Finding(L.T("SMART / 健康情報", "SMART / health"), Verdict.Unavailable, why));
            return new DiskJudgement(r, findings, deltas, previous, st);
        }

        if (r.Nvme is { } n) JudgeNvme(n, r.NvmeErrors, deltas, previous, noCompare, findings);
        else if (r.Ata.Count > 0) JudgeAta(r, deltas, previous, noCompare, findings);
        else findings.Add(new Finding(L.T("SMART / 健康情報", "SMART / health"), Verdict.Unavailable,
                                      L.T("値が返ってきませんでした", "the device returned no data")));

        return new DiskJudgement(r, findings, deltas, previous, st);
    }

    private static CounterDelta? DeltaOf(IReadOnlyList<CounterDelta> deltas, string key) =>
        deltas.FirstOrDefault(d => d.Counter == key);

    private static string Since(BaselineEntry prev) => prev.At.ToLocalTime().ToString("yyyy-MM-dd");

    // ---------------------------------------------------------------------
    // NVMe
    // ---------------------------------------------------------------------
    private static void JudgeNvme(NvmeHealth n, IReadOnlyList<NvmeErrorEntry> errors,
                                  IReadOnlyList<CounterDelta> deltas, BaselineEntry? prev, string? noCompare, List<Finding> f)
    {
        // 重大警告
        if (n.CriticalWarning != 0)
            f.Add(new Finding(L.T("重大警告", "Critical warning"), Verdict.Check,
                string.Join(L.T("、", ", "), NvmeHealth.CriticalWarningNames(n.CriticalWarning)) +
                $" (0x{n.CriticalWarning:X2})"));
        else
            f.Add(new Finding(L.T("重大警告", "Critical warning"), Verdict.Ok, L.T("立っていません", "none set")));

        // メディアエラー（訂正できなかった誤り）
        f.Add(GrowthFinding(L.T("メディアエラー", "Media errors"), n.MediaErrors, DeltaOf(deltas, Counters.MediaErrors), prev,
            zero: L.T("0 件", "none recorded"),
            grew: d => L.T($"{d.Format()}。前回（{Since(prev!)}）から増えています。大事なデータのバックアップを",
                           $"{d.Format()} since {Since(prev!)}. It is growing — back up important data"),
            stable: d => L.T($"累計 {L.N(n.MediaErrors)} は過去の記録。前回（{Since(prev!)}）から増えていません",
                             $"{L.N(n.MediaErrors)} in total, not grown since {Since(prev!)}"),
            first: noCompare is not null
                ? L.T($"累計 {L.N(n.MediaErrors)}。", $"{L.N(n.MediaErrors)} in total; ") + noCompare
                : L.T($"累計 {L.N(n.MediaErrors)}。増えているかは次回の点検で分かります（今回を基準に記録）",
                      $"{L.N(n.MediaErrors)} in total. The next check will show whether it is growing (recorded as baseline)"),
            firstVerdict: Verdict.Caution, grewVerdict: Verdict.Check));

        // 予備領域
        if (n.AvailableSpareThreshold > 0 && n.AvailableSpare < n.AvailableSpareThreshold)
            f.Add(new Finding(L.T("予備領域", "Available spare"), Verdict.Check,
                L.T($"残り {n.AvailableSpare}% が下限 {n.AvailableSpareThreshold}% を下回っています",
                    $"{n.AvailableSpare}% left, below the {n.AvailableSpareThreshold}% threshold")));
        else if (n.AvailableSpare < n.AvailableSpareThreshold + 10)
            f.Add(new Finding(L.T("予備領域", "Available spare"), Verdict.Caution,
                L.T($"残り {n.AvailableSpare}%（下限 {n.AvailableSpareThreshold}%）。下限に近い",
                    $"{n.AvailableSpare}% left (threshold {n.AvailableSpareThreshold}%), close to the limit")));
        else
            f.Add(new Finding(L.T("予備領域", "Available spare"), Verdict.Ok,
                L.T($"残り {n.AvailableSpare}%（下限 {n.AvailableSpareThreshold}%）",
                    $"{n.AvailableSpare}% left (threshold {n.AvailableSpareThreshold}%)")));

        // 寿命の消費
        var wear = n.PercentageUsed >= 100 ? Verdict.Check : n.PercentageUsed >= 90 ? Verdict.Caution : Verdict.Ok;
        f.Add(new Finding(L.T("寿命の消費", "Percentage used"), wear,
            n.PercentageUsed >= 100
                ? L.T($"{n.PercentageUsed}%。設計上の書き込み寿命を使い切っています（すぐ壊れるとは限りません）",
                      $"{n.PercentageUsed}% — rated write endurance used up (does not mean it fails right away)")
                : L.T($"{n.PercentageUsed}%（メーカーの見積もり）", $"{n.PercentageUsed}% (vendor estimate)")));

        // 温度
        if (n.CompositeTemperatureCelsius is { } c)
        {
            var tv = c >= 80 ? Verdict.Check : c >= 70 ? Verdict.Caution : Verdict.Ok;
            string extra = n.CriticalTempMinutes > 0
                ? L.T($"。危険温度を超えた時間の累計 {n.CriticalTempMinutes} 分", $"; {n.CriticalTempMinutes} min above critical temp in total")
                : n.WarningTempMinutes > 0
                    ? L.T($"。警告温度を超えた時間の累計 {n.WarningTempMinutes} 分", $"; {n.WarningTempMinutes} min above warning temp in total")
                    : "";
            if (tv == Verdict.Ok && n.CriticalTempMinutes > 0) tv = Verdict.Caution;
            f.Add(new Finding(L.T("温度", "Temperature"), tv, $"{c} °C{extra}"));
        }
        else f.Add(new Finding(L.T("温度", "Temperature"), Verdict.Unavailable, L.T("報告されていません", "not reported")));

        // エラーログ
        int mediaInLog = errors.Count(e => e.IsMediaError);
        var logDelta = DeltaOf(deltas, Counters.ErrorLogEntries);
        string logNote = L.T("（多くは機器が「未対応の命令」と返した記録で、壊れている印とは限りません）",
                             " (mostly 'unsupported command' replies, not necessarily damage)");
        if (mediaInLog > 0)
            f.Add(new Finding(L.T("エラーログ", "Error log"), Verdict.Caution,
                L.T($"最近の記録 {errors.Count} 件のうち {mediaInLog} 件が媒体の誤り", $"{mediaInLog} of the {errors.Count} recent entries are media errors")));
        else if (logDelta is { Grew: true })
            f.Add(new Finding(L.T("エラーログ", "Error log"), Verdict.Caution, logDelta.Format() + logNote));
        else
            f.Add(new Finding(L.T("エラーログ", "Error log"), Verdict.Ok,
                L.T($"累計 {L.N(n.ErrorLogEntries)} 件", $"{L.N(n.ErrorLogEntries)} entries in total") +
                (n.ErrorLogEntries > 0 ? logNote : "") +
                (logDelta is not null ? L.T("。前回から増えていません", ", not grown since last check") : "")));

        // 参考（判定しない）
        var unsafeDelta = DeltaOf(deltas, Counters.UnsafeShutdowns);
        f.Add(new Finding(L.T("不意の電源断", "Unsafe shutdowns"), Verdict.Ok,
            unsafeDelta is { Grew: true }
                ? unsafeDelta.Format() + L.T("（停電・長押しでの電源断など）", " (power loss, forced power-off)")
                : L.T($"累計 {L.N(n.UnsafeShutdowns)} 回", $"{L.N(n.UnsafeShutdowns)} in total")));
        f.Add(new Finding(L.T("通電時間・読み書き量", "Power-on / data"), Verdict.Ok,
            L.T($"{L.N(n.PowerOnHours)} 時間 / 読み {Tb(n.BytesRead)} / 書き {Tb(n.BytesWritten)}",
                $"{L.N(n.PowerOnHours)} h / read {Tb(n.BytesRead)} / written {Tb(n.BytesWritten)}")));
    }

    // ---------------------------------------------------------------------
    // ATA / SATA
    // ---------------------------------------------------------------------
    private static void JudgeAta(DiskReport r, IReadOnlyList<CounterDelta> deltas, BaselineEntry? prev, string? noCompare, List<Finding> f)
    {
        var attrs = r.Ata;
        if (r.PredictFailure == true)
            f.Add(new Finding(L.T("故障予測", "Failure prediction"), Verdict.Check,
                L.T("機器自身が「故障の見込みあり」と報告しています", "the drive itself reports a predicted failure")));

        // 機器自身の閾値判定
        var failing = attrs.Where(a => a.FailingNow).ToList();
        var failedPast = attrs.Where(a => a.FailedInPast).ToList();
        if (r.AtaSmartThresholds is null)
            f.Add(new Finding(L.T("閾値", "Thresholds"), Verdict.Unavailable,
                L.T("閾値は読めませんでした。生の値だけで判定しています", "thresholds could not be read; judging raw values only")));
        else if (failing.Count > 0)
            f.Add(new Finding(L.T("閾値", "Thresholds"), Verdict.Check,
                string.Join(L.T("、", ", "), failing.Select(a => $"{a.Name} {a.Current} ≤ {a.Threshold}")) +
                L.T("（機器自身が故障の見込みと判定）", " (the drive's own failure threshold)")));
        else if (failedPast.Count > 0)
            f.Add(new Finding(L.T("閾値", "Thresholds"), Verdict.Caution,
                string.Join(L.T("、", ", "), failedPast.Select(a => $"{a.Name} worst {a.Worst} ≤ {a.Threshold}")) +
                L.T("（過去に閾値を割ったことがある）", " (crossed the threshold in the past)")));
        else
            f.Add(new Finding(L.T("閾値", "Thresholds"), Verdict.Ok, L.T("閾値を割った属性はありません", "no attribute at or below its threshold")));

        foreach (byte id in AtaSmart.KeyIds)
        {
            var a = attrs.FirstOrDefault(x => x.Id == id);
            if (a is null) continue;
            var d = DeltaOf(deltas, Counters.Ata(id));
            var (first, nonzeroVerdict) = id switch
            {
                197 => (L.T("読めなくなりかけのセクタ。大事なデータのバックアップを", "sectors waiting to be remapped — back up important data"), Verdict.Check),
                198 => (L.T("読めなかったセクタ。大事なデータのバックアップを", "sectors that could not be read — back up important data"), Verdict.Check),
                199 => (L.T("ケーブルや差し込みの不良であることが多い", "usually a cable or connector problem"), Verdict.Caution),
                _ => (L.T("増えていなければ様子見でよい", "fine to watch if it is not growing"), Verdict.Caution),
            };
            var grewVerdict = Counters.GrowthVerdict(Counters.Ata(id)) ?? Verdict.Caution;
            f.Add(GrowthFinding(a.Name, a.Value, d, prev,
                zero: "0",
                grew: dd => L.T($"{dd.Format()}。前回（{Since(prev!)}）から増えています", $"{dd.Format()} since {Since(prev!)} — growing"),
                stable: dd => $"{L.N(a.Value)}" + L.T($"。前回（{Since(prev!)}）から増えていません", $", not grown since {Since(prev!)}") +
                              (nonzeroVerdict == Verdict.Check ? L.T("。ただし今も残っていること自体が問題です", ". Still, these sectors are unreadable right now") : ""),
                first: $"{L.N(a.Value)}。" + first + (noCompare is not null ? L.T("。", ". ") + noCompare : ""),
                firstVerdict: nonzeroVerdict,
                grewVerdict: grewVerdict,
                // 代替待ち・訂正不能は「今まさに読めないセクタがある」ので、増えていなくても軽くしない
                stableVerdict: nonzeroVerdict == Verdict.Check ? Verdict.Check : Verdict.Ok));
        }

        var temp = attrs.FirstOrDefault(a => a.Id == 194) ?? attrs.FirstOrDefault(a => a.Id == 190);
        if (temp is not null && temp.Value is > 0 and < 128)
        {
            var tv = temp.Value >= 70 ? Verdict.Check : temp.Value >= 60 ? Verdict.Caution : Verdict.Ok;
            f.Add(new Finding(L.T("温度", "Temperature"), tv, $"{temp.Value} °C"));
        }
        else f.Add(new Finding(L.T("温度", "Temperature"), Verdict.Unavailable, L.T("報告されていません", "not reported")));

        var poh = attrs.FirstOrDefault(a => a.Id == 9);
        if (poh is not null)
            f.Add(new Finding(L.T("通電時間", "Power-on hours"), Verdict.Ok, L.T($"{L.N(poh.Value)} 時間", $"{L.N(poh.Value)} h")));

        if (r.AtaFromPredictFailure)
            f.Add(new Finding(L.T("読み方", "Source"), Verdict.Ok,
                L.T("SMART 命令が通らなかったので、Windows の故障予測の窓口から読みました", "read through Windows' failure-prediction interface")));
    }

    /// <summary>
    /// 「増えたか」で判定する数え上げの共通の形。
    /// 0 なら OK、前回より増えていたら重く、増えていなければ軽く、初回なら「次回分かる」。
    /// </summary>
    private static Finding GrowthFinding(string title, ulong value, CounterDelta? delta, BaselineEntry? prev,
        string zero, Func<CounterDelta, string> grew, Func<CounterDelta, string> stable, string first,
        Verdict firstVerdict, Verdict grewVerdict, Verdict stableVerdict = Verdict.Ok)
    {
        if (delta is { Grew: true }) return new Finding(title, grewVerdict, grew(delta));
        if (value == 0) return new Finding(title, Verdict.Ok, zero);
        if (delta is not null && prev is not null) return new Finding(title, stableVerdict, stable(delta));
        return new Finding(title, firstVerdict, first);
    }

    private static string Tb(double bytes) =>
        bytes >= 1e12 ? $"{bytes / 1e12:0.0} TB" : $"{bytes / 1e9:0} GB";
}
