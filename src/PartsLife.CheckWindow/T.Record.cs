using PartsLife.Checks;
using PartsLife.Checks.Recording;

namespace PartsLife.CheckWindow;

/// <summary>「テストの記録」の面の文言。調子は点検の窓に合わせる: 短く、言い切る。分からないことは分からないと言う。</summary>
internal static partial class T
{
    public static string TabRecord => J("テストの記録", "Test recording");

    public static string RecordIntro => J(
        "ブラウザのベンチマークやストレステスト（web-autoai.com など）を動かしている間の、CPU・GPU・メモリ・ディスク・電源の様子を 1 秒ごとに記録し、終わったら手元で要約します。"
        + "管理者の権限もドライバも要りません。このアプリは何も送信しません。",
        "Logs CPU, GPU, memory, disk and power state once per second while you run a browser benchmark or stress test (for example on web-autoai.com), "
        + "then summarises it locally. No admin rights and no driver are needed. This app sends nothing.");
    public static string RecordHow => J(
        "使い方: ① 「テストの記録を開始」 ② ブラウザでベンチマークやストレステストを動かす（始まりと終わりに下の「目印」を押すと、区間ごとに要約できます） ③ 終わったら「記録を止める」",
        "How: (1) press \"Start test recording\"; (2) run the benchmark or stress test in your browser (press the marks below at its start and end to get per-span numbers); (3) press \"Stop recording\" when it ends.");
    public static string RecordFolderNote(string folder) => J(
        $"記録の保存先: {folder}（run-<ID>.json と sensors-<ID>.csv）。この窓を閉じると、記録は止まって、ここまでの分が保存されます。",
        $"Saved in: {folder} (run-<ID>.json and sensors-<ID>.csv). Closing this window stops the recording and keeps what was logged so far.");

    public static string RecordCheck => J("読めるセンサーを確認", "Check readable sensors");
    public static string RecordStart => J("テストの記録を開始", "Start test recording");
    public static string RecordStop => J("記録を止める", "Stop recording");
    public static string RecordOpenRun => J("過去の記録を開く…", "Open a past recording…");
    public static string RecordOpenFolder => J("記録のフォルダを開く", "Open recordings folder");
    public static string RecordOpenRunTitle => J("過去の記録を開く", "Open a past recording");

    public static string RecordPreparing => J("センサーを確認しています…", "Checking sensors…");
    public static string RecordLoading => J("記録を読み込んでいます…", "Loading the recording…");
    public static string RecordStopping => J("止めて、要約を作っています…", "Stopping and summarising…");
    public static string RecordRunning(TimeSpan t, int rows) => J($"記録中 {Elapsed(t)} · {rows} 行", $"Recording {Elapsed(t)} · {rows} rows");
    public static string RecordChecked(int ok, int ext, int no) => J(
        $"読めます {ok} · 追加機能が必要 {ext} · 読めません {no}", $"readable {ok} · needs extension {ext} · not readable {no}");
    public static string RecordDone(string stopReason) => stopReason switch
    {
        "max-duration" => J("上限の時間に達したので止めました。ここまでの記録を保存しました", "Stopped at the maximum duration. The recording is saved"),
        "io-error" => J("ファイルに書けなくなったので止めました。ここまでの記録は保存されています", "Stopped because the file could not be written. What was logged so far is saved"),
        "error" => J("読み取りでエラーが出たので止めました。ここまでの記録は保存されています", "Stopped because of a read error. What was logged so far is saved"),
        _ => J("止めました。記録を保存しました", "Stopped. The recording is saved"),
    };
    public static string RecordStartFailed(RecordException ex) => ex.Code switch
    {
        "low-disk" => J("保存先の空きが足りないので、始めませんでした（200 MB 以上の空きが要ります）", "Not started: the destination has too little free space (200 MB needed)"),
        "folder" => J("保存先のフォルダを作れない、または書けないので、始めませんでした: ", "Not started: the destination folder cannot be created or written: ") + ex.Message,
        _ => J("始められませんでした: ", "Could not start: ") + ex.Message,
    };

    // --- 読めるセンサー ---
    public static string CapHeading => J("読めるセンサー", "Readable sensors");
    public static string CapNote => J(
        "読めなかった物は、推測で埋めません。「追加機能が必要」は CPU の温度・電圧・ファンなどで、カーネルドライバが要るため、この版では読みません。",
        "Anything that cannot be read is not guessed. \"Needs extension\" (CPU temperature, voltages, fans…) needs a kernel driver, so this version does not read it.");
    public static string CapReadable => J("読めます", "Readable");
    public static string CapNone => J("なし", "none");
    public static string GroupCpu => "CPU";
    public static string GroupSystem => J("メモリ・ディスク・電源", "Memory, disk, power");
    public static string GroupExtension => J("追加機能が必要な項目", "Needs an extension");
    public static string GpuGroup(GpuDescriptor g) => $"GPU{g.Index} · {g.Name}" + (g.VramMb > 0 ? $" · {g.VramMb / 1024.0:0.0} GB" : "");

    // --- 目印 ---
    public static string MarksHeading => J("目印（任意）", "Marks (optional)");
    public static string MarksHelp => J(
        "ベンチマークやストレステストの始まりと終わりで押すと、あとで区間ごとに要約できます。決まった種類だけで、自由な文字は残しません。",
        "Press at the start and end of the benchmark or stress test to get per-span numbers later. Fixed kinds only; no free text is stored.");
    public static string MarkAdded(string label, TimeSpan t) => $"{label} · {Elapsed(t)}";

    // --- いまの値 ---
    public static string LiveHeading => J("いまの値", "Now");

    // --- 要約 ---
    public static string ResultHeading => J("要約", "Summary");
    public static string ResultFromFile => J("（過去の記録）", "(past recording)");
    public static string ResultNote => J(
        "規則による要約です。故障の診断ではありません。「同時に起きた」ことは、原因を意味しません。",
        "A rule-based summary. It is not a fault diagnosis. \"Happened at the same time\" does not mean \"caused by\".");
    public static string ResultFacts(RunSummary s) => J(
        $"記録 {Elapsed(TimeSpan.FromSeconds(s.DurationSec))} · {s.Samples} 行（{s.IntervalSec:0.#} 秒ごと）",
        $"Recorded {Elapsed(TimeSpan.FromSeconds(s.DurationSec))} · {s.Samples} rows (every {s.IntervalSec:0.#} s)");
    public static string ColMax => J("最大", "Max");
    public static string ColAvg => J("平均", "Avg");
    public static string ColSustained => J("持続", "Sustained");
    public static string SustainedHelp(double sec) => J(
        $"持続 = 負荷が高かった最後の {sec:0} 秒の、負荷が高かった時点の中央値。",
        $"Sustained = median over the last {sec:0} s of high load, using only the moments under high load.");
    public static string SustainedNone => J("持続した値は出していません（記録が短い、または負荷が高い時間が短い）。", "No sustained values (recording too short, or high load too brief).");
    public static string MostlyIdle => J("ほとんど使われていません", "barely used");
    public static string LimitsHeading => J("制限の印（ドライバ・OS の報告）", "Limit flags (reported by driver / Windows)");
    public static string LimitsNone => J("印は立ちませんでした", "no flags were set");
    public static string LimitLine(string name, double sec, double busy) => J(
        $"{RecordText.LimitName(name, "ja")}: {sec:0} 秒（うち負荷が高かった間 {busy:0} 秒）",
        $"{RecordText.LimitName(name, "en")}: {sec:0} s ({busy:0} s of it under high load)");
    public static string CoHeading => J("温度が上がりながらクロックが下がっていた時間帯", "Time windows where temperature rose while the clock fell");
    public static string CoNone => J("見つかりませんでした", "none found");
    public static string SpansHeading => J("目印で区切った区間", "Spans between marks");
    public static string SpanLabel(string kind, bool open) => (kind == "bench" ? J("ベンチ", "Bench") : J("ストレス", "Stress")) + (open ? J("（終わりの印なし）", " (no end mark)") : "");
    public static string NotesHeading => J("注記", "Notes");
    public static string PerCoreLine(PerCoreSummary p) => J(
        $"論理プロセッサ {p.LogicalProcessors} 個の平均使用率: 最大 {p.AvgOfMax:0}% · 最小 {p.AvgOfMin:0}% · 平均 {p.AvgOfMean:0}%（50% 以上が {p.ActiveCount} 個）",
        $"Average load over {p.LogicalProcessors} logical processors: max {p.AvgOfMax:0}% · min {p.AvgOfMin:0}% · mean {p.AvgOfMean:0}% ({p.ActiveCount} at 50% or more)");
    public static string FilesHeading => J("保存した記録", "Saved files");
    public static string SummaryFailed(string why) => J("要約を作れませんでした: ", "Could not summarise: ") + why;

    // --- AI に渡す用 ---
    public static string AiBuild => J("AIに渡す用を作る", "Build text for an AI");
    public static string AiHeading => J("AIに渡す用（プレビュー）", "For an AI (preview)");
    public static string AiIntro => J(
        "下の内容が、そのままコピー・保存されます。許可した項目だけで、シリアル番号・PC の名前・利用者名・ファイルのパス・プロセス名・自由に書いた文字は入りません。"
        + "このアプリは送信しません。AI への貼り付けやファイルの添付は、内容を確かめたうえでご自分でしてください。",
        "Exactly what you see below is what gets copied or saved. Only allowlisted fields are included: no serial numbers, PC name, user name, file paths, process names or free text. "
        + "This app sends nothing; paste or attach it to your AI yourself after checking it.");
    public static string AiCopy => J("コピー", "Copy");
    public static string AiSave => J("ファイルに保存…", "Save as file…");
    public static string AiSaveTitle => J("AIに渡す用を保存", "Save text for an AI");
    public static string AiBuilding => J("作っています…", "Building…");
    public static string AiCopied => J("コピーしました。AI に貼り付けてください", "Copied. Paste it into your AI");
    public static string AiSaved(string file) => J($"保存しました: {file}", $"Saved: {file}");
    public static string AiBlocked => J(
        "個人を特定しうる文字列が紛れたため、書き出しを止めました（何も保存していません）。",
        "The export was stopped because text that could identify you slipped in (nothing was saved).");
    public static string AiWithheld(int n) => J($"個人を特定しうる名前 {n} 件を、書き出しから外しました。", $"{n} name(s) that could identify you were left out.");

    public static string Elapsed(TimeSpan t)
    {
        int s = (int)Math.Max(0, t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}
