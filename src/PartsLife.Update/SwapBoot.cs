namespace PartsLife.Update;

/// <summary>起動したときに、入れ替えの記録（swap.json）を見て何をするか。</summary>
public enum BootAction
{
    /// <summary>記録なし。残っている .old などを片付けて、普通に起動する。</summary>
    None,
    /// <summary>入れ替えた新しい版として起動した。起動の回数を数え、しばらく無事に動いたら確かめ済みにする。</summary>
    Verify,
    /// <summary>新しい版が確かめの済む前に何度も起動している（落ち続けている）。元の版に戻す。</summary>
    Rollback,
    /// <summary>確かめ済み。.old を消して記録を片付ける（「次にうまく起動したときに .old を消す」）。</summary>
    Cleanup,
    /// <summary>記録が今の版と合わない（戻した後など）。記録だけ消す。</summary>
    Stale,
}

/// <summary>
/// 入れ替えの後の起動の決まり。**純粋な関数**（試験で中身を確かめる）。
///
/// 入れ替えの流れ:
///   1. 古い版が入れ替えて swap.json を書き、新しい版を <c>--updated-from 古い版</c> で起こす
///   2. 古い版は窓を隠して待つ（<see cref="WatchTimeout"/> まで）
///   3. 新しい版は窓を出し、<see cref="HealthyAfter"/> 無事に動いたら Confirmed を書く
///   4. 古い版はそれを見て終わる。新しい版は古い版が終わるのを待って .old を消す
/// 新しい版が途中で終わった・時間内に Confirmed を書かなかった → 古い版が戻して、元の版を起こし直す。
/// 古い版が見張れなかった（電源が落ちた等）ときは、新しい版の起動の回数で見る（<see cref="MaxUnconfirmedLaunches"/>）。
/// </summary>
public static class SwapBoot
{
    /// <summary>新しい版が窓を出してから、これだけ無事に動けば「起動できた」とする。</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(15);

    /// <summary>古い版が待つ時間。遅い HDD でも単一ファイルの展開と <see cref="HealthyAfter"/> が収まる長さ。</summary>
    public static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(120);

    /// <summary>確かめが済まないまま、この回数目の起動になったら戻す。</summary>
    public const int MaxUnconfirmedLaunches = 3;

    public static BootAction Decide(SwapRecord? record, AppVersion running)
    {
        if (record is null) return BootAction.None;
        if (record.Confirmed) return BootAction.Cleanup;
        if (!AppVersion.TryParse(record.To, out var to) || to != running) return BootAction.Stale;
        return record.Launches + 1 >= MaxUnconfirmedLaunches ? BootAction.Rollback : BootAction.Verify;
    }
}
