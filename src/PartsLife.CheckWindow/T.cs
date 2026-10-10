using PartsLife.Checks;
using PartsLife.Checks.Build;
using PartsLife.Checks.Protocol;
using PartsLife.Checks.Scan;

namespace PartsLife.CheckWindow;

/// <summary>
/// 窓の文言。本体の I18n.Strings と同じ流儀（日本語が先、英語も持つ）。
/// 調子も本体に合わせる: 短く、言い切る。分からないことは分からないと言う。
/// </summary>
internal static class T
{
    private static string J(string ja, string en) => L.T(ja, en);

    public static string WindowTitle => J("点検 — パーツぐらし", "Checkup — Parts Life");
    public static string WindowHeading => J("点検", "Checkup");
    public static string WindowSub => J("押したときだけ動きます。閉じれば全部止まります。ディスクには書き込みません。",
                                        "Nothing runs until you press a button. Closing stops everything. Nothing is written to your disks.");
    public static string TabSystem => J("システムチェック", "System check");
    public static string TabLoad => J("負荷テスト", "Load test");
    public static string TabStorage => J("記憶媒体の精密チェック", "Storage health");

    public static string Copy => J("結果をコピー", "Copy results");
    public static string Start => J("始める", "Start");
    public static string Stop => J("止める", "Stop");
    public static string Working => J("調べています…", "Checking…");
    public static string Starting => J("始めています…", "Starting…");
    public static string Stopped => J("止めました", "Stopped");
    public static string StoppedPartial => J("止めました。ここまでの結果です", "Stopped. Results so far:");
    public static string Failed => J("うまくいきませんでした: ", "Failed: ");
    public static string Finished(TimeSpan t) => J($"終わりました（{t.TotalSeconds:0} 秒）", $"Finished ({t.TotalSeconds:0} s)");

    public static string Summary(IEnumerable<Finding> f)
    {
        var list = f.ToList();
        int ok = list.Count(x => x.Verdict == Verdict.Ok), c = list.Count(x => x.Verdict == Verdict.Caution),
            k = list.Count(x => x.Verdict == Verdict.Check), n = list.Count(x => x.Verdict == Verdict.Unavailable);
        return J($"OK {ok} · 注意 {c} · 要確認 {k} · 測れず {n}", $"OK {ok} · Note {c} · Check {k} · N/A {n}");
    }

    // --- システムチェック ---
    public static string SystemIntro => J(
        "管理者の権限なしで読める範囲の健康診断です。OS・CPU・メモリ・GPU・ディスク・ページファイルと、直近 7 日のイベントログの件数を見ます。数秒かかります。",
        "A quick health snapshot using only what Windows shows without admin rights: OS, CPU, memory, GPU, disks, pagefile and the last 7 days of the event log. Takes a few seconds.");
    public static string SystemRun => J("調べる", "Check");
    public static string SystemNotMeasured => Features.StorageCheck
        ? J("測っていないもの: CPU の温度（カーネルドライバが要るため）。ディスクの SMART の数値は「記憶媒体の精密チェック」で（管理者の許可が要ります）。",
            "Not measured: CPU temperature (needs a kernel driver). Disk SMART counters are in \"Storage health\" (needs admin approval).")
        : J("測っていないもの: CPU の温度（カーネルドライバが要るため）と、ディスクの SMART の数値（管理者の許可が要るため。次の版で）。",
            "Not measured: CPU temperature (needs a kernel driver) and disk SMART counters (need admin approval; coming in the next version).");

    // --- 構成コード（web-autoai.com のマイPC） ---
    public static string BuildHeading => J("構成コード（web-autoai.com のマイPCに登録）", "Build code (register as My PC on web-autoai.com)");
    public static string BuildIntro => J(
        "この PC の部品の型番（CPU・GPU・マザーボード・メモリ・SSD・HDD・モニター）を、貼り付けられる文字列にします。シリアル番号・PC の名前・利用者名・ドライブの中身は入りません。このアプリは送信しません。コピーするかファイルに保存して、web-autoai.com の マイPC登録 に貼り付けてください。",
        "Turns this PC's part models (CPU, GPU, motherboard, memory, SSD, HDD, monitors) into a string you can paste. No serial numbers, PC name, user name or drive contents are included. This app sends nothing: copy it or save it to a file, then paste it into My PC on web-autoai.com.");
    public static string BuildCopy => J("構成コードをコピー", "Copy build code");
    public static string BuildSave => J("構成をJSONで保存", "Save build as JSON");
    public static string BuildShow => J("中身を見る", "Show contents");
    public static string BuildHide => J("中身を隠す", "Hide contents");
    public static string BuildReading => J("部品を読んでいます…", "Reading parts…");
    public static string BuildNothing => J("部品を読み取れませんでした", "No parts could be read");
    public static string BuildClipboardFailed => J("クリップボードに書けませんでした。もう一度押してください", "Could not write to the clipboard. Please try again");
    public static string BuildSaveTitle => J("構成を保存", "Save build");
    public static string BuildCopied(string summary) => J(
        $"{summary} をコピーしました。web-autoai.com の マイPC登録 に貼り付けてください",
        $"Copied {summary}. Paste it into My PC on web-autoai.com");
    public static string BuildSaved(string file) => J($"保存しました: {file}", $"Saved: {file}");
    public static string BuildCodeLabel => J("コード（これをコピーします）", "Code (this is what is copied)");
    public static string BuildJsonLabel => J("中身（同じ物を読める形で）", "Contents (the same, readable)");

    /// <summary>「CPU 1・GPU 1・マザーボード 1・メモリ 2枚・SSD 1」。枚数は q を足した数。</summary>
    public static string BuildSummary(BuildConfig c) => string.Join(J("・", ", "),
        c.NonEmpty.Select(cat => cat == "ram"
            ? J($"メモリ {c.Count(cat)}枚", $"memory {c.Count(cat)} module(s)")
            : $"{BuildCode.CategoryName(cat)} {c.Count(cat)}"));

    // --- 負荷テスト ---
    public static string LoadIntro => J(
        "部品に決まった時間だけ仕事をさせて、持ちこたえるか・計算を間違えないかを見ます。止めるボタンはすぐ効きます。キャラクターたちがばたばたし始めますが、本当に働いているからです。",
        "Puts one part under load for a set time and checks that it keeps up and does not miscalculate. Stop takes effect immediately. The characters will start bustling — they really are working.");
    public static string Kind => J("種類", "Part");
    public static string Duration => J("時間", "Length");
    public static string Amount => J("量", "Amount");
    public static string Memory => J("メモリ", "Memory");
    public static string Seconds(int s) => J($"{s} 秒", $"{s} s");
    public static string Minutes(int m) => J($"{m} 分", $"{m} min");
    public static string OfFree(int pct) => J($"空きの {pct}%", $"{pct}% of free");
    public static string GpuNote => J(
        "GPU の試験は控えめです: 働かせるのは 75% の時間だけ、83 ℃ で自動的に止めます。温度を見張れない GPU（いまは NVIDIA 以外）には負荷をかけません。",
        "The GPU test is conservative: 75% duty, automatic stop at 83 °C. GPUs whose temperature cannot be watched (currently anything but NVIDIA) are not loaded.");
    public static string NoGpu => J("GPU が見つかりません", "No GPU found");
    public static string CannotWatch(bool nvidia) => nvidia
        ? J("NVIDIA のドライバから温度を読めないため、試験しません", "Temperature cannot be read from the NVIDIA driver, so this GPU is not tested")
        : J("温度を見張れないため、試験しません（AMD/Intel は今後対応予定）", "Temperature cannot be watched, so this GPU is not tested (AMD/Intel: to do)");
    public static string LoadFootnote => J(
        "数値はこの試験独自の単位で、ほかのベンチマークとは比べられません。メモリの試験は OS が貸してくれた領域だけが対象です。",
        "Numbers are in this test's own units and are not comparable to other benchmarks. The memory test only covers the region Windows lends it.");
    public static string Clock => J("実効", "clock");
    public static string NotExposed => J("（非公開）", "(not exposed)");
    public static string Load => J("使用率", "load");
    public static string Total => J("合計", "total");
    public static string Temperature => J("温度", "temp");
    public static string Writing => J("書き込み中", "writing");
    public static string Verifying => J("照合中", "verifying");
    public static string Pass(int n) => J($"{n} 周目", $"pass {n}");
    public static string Mismatches(long n) => J($"食い違い {L.N(n)}", $"{L.N(n)} mismatches");
    public static string ComputeErrors(long n) => J($"計算違い {L.N(n)}", $"{L.N(n)} wrong results");

    // --- 記憶媒体 ---
    public static string StorageIntro => J(
        "記憶媒体の健康情報（NVMe の Health ログ、SATA の SMART）を読みます。Windows の決まりで管理者の許可が要るため、押すと確認（UAC）が出ます。読むだけで、書き込みはしません。数字は前回の点検と比べ、増えたかどうかを見ます。",
        "Reads the drives' own health records (NVMe health log, SATA SMART). Windows requires admin approval for this, so a UAC prompt appears. Read-only. Counters are compared with the previous check to see whether they grew.");
    public static string ReadSmart => J("健康情報を読む（管理者）", "Read health data (admin)");
    public static string AskingUac => J("管理者の許可を待っています…", "Waiting for admin approval…");
    public static string HelperMissing => J(
        "PartsLife.Check.exe が見つかりません。ZIP 版に同梱しています（単体 exe 版には入っていません）。",
        "PartsLife.Check.exe was not found. It ships in the ZIP version (not in the single-exe download).");
    public static string SmartDone(int n) => J($"{n} 台を読みました", $"Read {n} drive(s)");
    public static string SinceLast(string when) => J($"前回（{when}）からの変化:", $"Changes since last check ({when}):");
    public static string NoChange => J("変化なし", "no change");
    public static string FirstBaseline => J("初めての点検です。今回の値を基準として記録しました。次回から増減を表示します。",
                                            "First check for this drive. Recorded as the baseline; changes will show next time.");
    public static string RecordFailed => J("基準値を記録できませんでした: ", "Could not record the baseline: ");
    public static string AttributeTable => J("属性の一覧", "All attributes");
    public static string BelowThreshold => J("閾値以下", "at/below threshold");
    public static string StorageFootnote(string dir) => J(
        $"基準値の置き場所: {dir}（シリアル番号ごと）。USB の外付けなど、間に変換基板があると読めないことがあります。そのときは推測せず「読めません」と出します。",
        $"Baselines are kept in {dir} (per serial number). Drives behind USB bridges often cannot be read; they are shown as not available rather than guessed.");

    public static string ScanHeading => J("表面検査（読むだけ）", "Surface scan (read-only)");
    public static string ScanIntro => J(
        "ディスクを先頭から読み、読めない所と、読むのに手間取る所を探します。キャッシュを通さずに媒体そのものを読みます。書き込みはしません。これも管理者の許可が要ります。",
        "Reads the disk from start to end looking for unreadable and slow spots, bypassing the cache. Nothing is written. Also needs admin approval.");
    public static string Disk => J("ディスク", "Disk");
    public static string Mode => J("範囲", "Range");
    public static string Sampled => J("間引き（256 ブロックに 1 つ）", "Sampled (1 in 256 blocks)");
    public static string Full => J("全域（時間がかかります）", "Full (takes long)");
    public static string ScanStart => J("表面検査を始める（管理者）", "Start surface scan (admin)");
    public static string ScanDone => J("終わりました", "Finished");
    public static string NoDisks => J("ディスクの一覧を読めませんでした", "Could not list disks");
    public static string ScanLive(ScanProgress p) => J(
        $"{p.DoneBytes / 1e9:0.0} / {p.PlannedBytes / 1e9:0.0} GB · {p.MBps:0} MB/s · 読めない所 {L.N(p.Errors)} · 遅い所 {L.N(p.Slow)}",
        $"{p.DoneBytes / 1e9:0.0} / {p.PlannedBytes / 1e9:0.0} GB · {p.MBps:0} MB/s · unreadable {L.N(p.Errors)} · slow {L.N(p.Slow)}");

    public static string Outcome(HelperOutcome o, string? detail) => o switch
    {
        HelperOutcome.Declined => J("許可されなかったので、何もしていません", "Not approved, so nothing was done"),
        HelperOutcome.Missing => HelperMissing,
        HelperOutcome.Stopped => Stopped,
        HelperOutcome.Disabled => J("この版では記憶媒体の精密チェックを止めてあります", "Storage health is disabled in this build"),
        _ => J("補助プログラムから結果が届きませんでした", "No result came back from the helper") + (detail is null ? "" : $" ({detail})"),
    };
}
