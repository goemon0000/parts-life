namespace PartsLife.Checks;

/// <summary>
/// 点検のうち、**この版で出す機能の切り替え。ここ1か所だけ。**
///
/// 実機（オーナーの機械）で、人が付き添って確かめた物だけを true にする。
///
/// - <see cref="StorageCheck"/>: 記憶媒体の精密チェック（管理者の補助 exe PartsLife.Check.exe、
///   NVMe/SMART の読み取り、基準値との差、表面検査）。v0.7.0 は止めて出し、2026-10-09 に実機で
///   付き添い試験を済ませて **v0.7.1 で有効にした**（4 台の読み取り・2 回目の基準値の差・
///   標本の表面検査の開始と停止・UAC を断ったとき・終わった後に補助 exe が残らないこと）。
///   false にすると点検の窓に面が出ず、<see cref="Protocol.HelperSession"/> も補助 exe を起こさない（二重の守り）。
///   CI の release.yml は補助 exe の発行・署名・ZIP への同梱をしているので、false に戻すならそちらも外す
/// - <see cref="GpuLoadTest"/>: GPU の負荷テスト。**まだ実機で確かめていないので false のまま。**
///   false の間は負荷テストの面に GPU の選択肢が出ない
///
/// **環境変数・レジストリ・起動引数で切り替える道は作らない。** 昇格の経路のそばに
/// 外から書き換えられるスイッチを置くと、それ自体が穴になる（以前の点検での指摘）。
/// 定数なので、配った exe の中で変えることはできない。
/// </summary>
internal static class Features
{
    /// <summary>記憶媒体の精密チェック。2026-10-09 の実機の付き添い試験のあと、v0.7.1 で有効にした。</summary>
    internal const bool StorageCheck = true;

    /// <summary>GPU の負荷テスト。実機の付き添い試験がまだなので止めたまま（次の版で、確かめたあと true に）。</summary>
    internal const bool GpuLoadTest = false;
}
