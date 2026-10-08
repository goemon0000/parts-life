namespace PartsLife.Checks.Load;

/// <summary>
/// 持続性。**序盤と終盤の平均の比**と、全体のばらつき（変動係数）。
/// </summary>
/// <param name="Score">終盤 ÷ 序盤 × 100（100 で頭打ち）。下がっていれば絞られている。</param>
/// <param name="Cv">標準偏差 ÷ 平均。</param>
public sealed record SteadinessResult(double Score, double Cv, double FirstAverage, double LastAverage, int Samples);

public static class Steadiness
{
    /// <summary>
    /// 1秒ごとの値の並びから持続性を出す。**純粋な関数。**
    /// 最初の <paramref name="skip"/> 個は捨てる（立ち上がりの途中なので）。
    /// 残りが 6 個未満なら判定しない（null）。短すぎる試験で「安定」と言うのは嘘になる。
    /// </summary>
    public static SteadinessResult? Compute(IReadOnlyList<double> series, int skip = 2)
    {
        var v = series.Skip(skip).ToArray();
        if (v.Length < 6) return null;
        int third = Math.Max(2, v.Length / 3);
        double first = v.Take(third).Average();
        double last = v.Skip(v.Length - third).Average();
        double mean = v.Average();
        double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
        double score = first <= 0 ? 0 : Math.Clamp(last / first, 0, 1) * 100;
        return new SteadinessResult(score, mean <= 0 ? 0 : sd / mean, first, last, v.Length);
    }

    public static Verdict Judge(SteadinessResult r) =>
        r.Score < 70 ? Verdict.Check : r.Score < 90 || r.Cv > 0.10 ? Verdict.Caution : Verdict.Ok;
}
