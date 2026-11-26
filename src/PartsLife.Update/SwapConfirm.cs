namespace PartsLife.Update;

/// <summary>「起動できた」（swap.json の Confirmed）を書いた結果。</summary>
public enum ConfirmOutcome
{
    /// <summary>書けた（または、もう書いてあった）。</summary>
    Confirmed,

    /// <summary>この版に向けた入れ替えの記録が無い（入れ替えの確かめの最中ではない）。</summary>
    NoPendingSwap,

    /// <summary>何度やっても書けなかった。案内は出さず、戻すのは見張っている古い版に任せる。</summary>
    Failed,
}

/// <summary>
/// 入れ替えた新しい版が「起動できた」と書く。**一度の失敗で諦めない**: 見張っている古い版は、書かれなければ
/// <see cref="SwapBoot.WatchTimeout"/> のあとに元へ戻す。ウイルス対策ソフトの走査などで、書き込みが一瞬だけ失敗することがある。
/// 5 回（200 ミリ秒おき）で約 1 秒。見張りの 120 秒に十分収まる。画面のスレッドの外で呼ぶ（待つので）。
/// </summary>
public static class SwapConfirm
{
    public const int Tries = 5;

    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(200);

    /// <param name="currentVersion">いまの版（記録の To と同じなら、この版に向けた入れ替え）。</param>
    /// <param name="record">書けた（または書いてあった）記録。</param>
    /// <param name="sleep">待ち方（試験では差し替える）。省略は <see cref="Thread.Sleep(TimeSpan)"/>。</param>
    public static ConfirmOutcome TryConfirm(UpdateStore store, string currentVersion, out SwapRecord? record,
        int tries = Tries, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        record = null;
        for (int i = 0; i < tries; i++)
        {
            if (i > 0) sleep(Gap);
            var rec = store.LoadSwap();
            if (rec is null)
            {
                // 記録が無い（入れ替えではない）なら終わり。ファイルはあるのに読めないのは一時的な失敗かもしれない → もう一度
                if (!File.Exists(store.SwapPath)) return ConfirmOutcome.NoPendingSwap;
                continue;
            }
            if (rec.To != currentVersion) return ConfirmOutcome.NoPendingSwap;
            if (rec.Confirmed)
            {
                record = rec;
                return ConfirmOutcome.Confirmed;
            }
            rec.Confirmed = true;
            if (store.SaveSwap(rec))
            {
                record = rec;
                return ConfirmOutcome.Confirmed;
            }
        }
        return ConfirmOutcome.Failed;
    }
}
