namespace PartsLife.Checks;

/// <summary>
/// 点検のうち、**この版で出す機能の切り替え。ここ1か所だけ。**
///
/// v0.7.0 では次の2つを止めて出す。どちらも実機（オーナーの機械）で、人が付き添って
/// 確かめるまでは配らない、と決めたため。コード・ライブラリ・試験はそのまま残してあり、
/// **v0.7.1 で付き添いの実機試験を済ませたら、下の定数を true にするだけで戻る**
/// （CI の release.yml で補助 exe の発行・署名・同梱も戻す。手順はそちらに書いてある）。
///
/// - <see cref="StorageCheck"/>: 記憶媒体の精密チェック（管理者の補助 exe PartsLife.Check.exe、
///   NVMe/SMART の読み取り、表面検査）。false の間は点検の窓に面が出ず、
///   <see cref="Protocol.HelperSession"/> も補助 exe を起こさない（二重の守り）
/// - <see cref="GpuLoadTest"/>: GPU の負荷テスト。false の間は負荷テストの面に GPU の選択肢が出ない
///
/// **環境変数・レジストリ・起動引数で切り替える道は作らない。** 昇格の経路のそばに
/// 外から書き換えられるスイッチを置くと、それ自体が穴になる（以前の点検での指摘）。
/// 定数なので、配った exe の中で変えることはできない。
/// </summary>
internal static class Features
{
    /// <summary>記憶媒体の精密チェック。v0.7.1 で実機の付き添い試験のあと true に。</summary>
    internal const bool StorageCheck = false;

    /// <summary>GPU の負荷テスト。v0.7.1 で実機の付き添い試験のあと true に。</summary>
    internal const bool GpuLoadTest = false;
}
