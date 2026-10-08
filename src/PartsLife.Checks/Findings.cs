namespace PartsLife.Checks;

/// <summary>
/// 判定は3段階だけ。**色と言葉を増やすと、どれが本当に大事なのか読めなくなる。**
/// </summary>
public enum Verdict
{
    /// <summary>測れて、問題は見当たらない。</summary>
    Ok = 0,
    /// <summary>気にしておくとよい。壊れているとは言っていない。</summary>
    Caution = 1,
    /// <summary>一度ちゃんと見た方がよい。</summary>
    Check = 2,
    /// <summary>
    /// 測れなかった。**「問題なし」とは別物。** 読めなかった物を OK と出すと嘘になる。
    /// </summary>
    Unavailable = 3,
}

/// <summary>判定1件。理由は1行で、言い切る。</summary>
public sealed record Finding(string Title, Verdict Verdict, string Reason, string? Detail = null);

/// <summary>
/// 文言の言語。本体の I18n.Strings と同じ流儀（ja / en、既定は ja）。
/// 本体側の型は参照できない（本体がこちらを参照している）ので、窓を開くときに渡してもらう。
/// </summary>
public static class L
{
    public static string Lang { get; set; } = "ja";
    public static string T(string ja, string en) => Lang == "en" ? en : ja;

    public static string VerdictLabel(Verdict v) => v switch
    {
        Verdict.Ok => "OK",
        Verdict.Caution => T("注意", "Note"),
        Verdict.Check => T("要確認", "Check"),
        _ => T("測れず", "N/A"),
    };

    /// <summary>3桁区切り。言語に関わらず同じ書き方にする（「363,278」）。</summary>
    public static string N(ulong v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public static string N(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
