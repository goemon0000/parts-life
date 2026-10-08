namespace PartsLife.Checks.Scan;

/// <summary>
/// 表面検査で読む場所の一覧。**純粋な計算だけ。**
///
/// 全域モード: 先頭から末尾まで隙間なく。
/// 間引きモード: <c>every</c> ブロックに1つ。ただし**最後のブロックは必ず読む**
/// （末尾だけ読めない壊れ方を見落とさないため）。
/// </summary>
public static class ScanPlan
{
    public const int DefaultBlockBytes = 1 << 20;   // 1 MiB

    public static long BlockCount(long totalBytes, int blockBytes) =>
        totalBytes <= 0 ? 0 : (totalBytes + blockBytes - 1) / blockBytes;

    /// <summary>
    /// 読む (位置, 長さ)。長さは <paramref name="sectorBytes"/> の倍数に切り詰める
    /// （FILE_FLAG_NO_BUFFERING はセクタ単位でしか読めない）。
    /// </summary>
    public static IEnumerable<(long Offset, int Length)> Blocks(long totalBytes, int blockBytes, int every, int sectorBytes)
    {
        if (blockBytes <= 0 || sectorBytes <= 0 || blockBytes % sectorBytes != 0)
            throw new ArgumentException("block size must be a positive multiple of the sector size");
        if (every < 1) every = 1;
        long total = totalBytes - totalBytes % sectorBytes;   // 半端なセクタは読めない
        long count = BlockCount(total, blockBytes);
        for (long i = 0; i < count; i++)
        {
            if (i % every != 0 && i != count - 1) continue;
            long offset = i * blockBytes;
            int length = (int)Math.Min(blockBytes, total - offset);
            if (length <= 0) yield break;
            yield return (offset, length);
        }
    }

    /// <summary>実際に読むバイト数（進み具合の分母）。数えるだけなので 2TB の全域でも数ミリ秒。</summary>
    public static long PlannedBytes(long totalBytes, int blockBytes, int every, int sectorBytes) =>
        Blocks(totalBytes, blockBytes, every, sectorBytes).Sum(b => (long)b.Length);
}

/// <summary>
/// 読み取りの遅さの記録。**中央値の何倍か**で「遅いブロック」を決める。
/// SSD と HDD では普通の速さが桁で違うので、決め打ちの閾値は使えない。
///
/// 中央値は対数目盛りの度数分布から出す（全件を覚えると 2TB で数百万件になる）。
/// </summary>
public sealed class LatencyTracker
{
    // 0.01 ms 〜 約 100 s を、2 倍あたり 8 段の対数目盛りで
    private const double MinMs = 0.01;
    private const int PerOctave = 8;
    private const int Buckets = 8 * 24;
    private readonly long[] _hist = new long[Buckets];

    /// <summary>この回数を読むまでは「遅い」と判定しない（基準が定まらない）。</summary>
    public int WarmUp { get; init; } = 32;
    /// <summary>中央値のこの倍数を超えたら遅い。</summary>
    public double Factor { get; init; } = 10;
    /// <summary>これより速ければ、倍数に関わらず遅いとは言わない。</summary>
    public double FloorMs { get; init; } = 50;

    public long Count { get; private set; }
    public double MaxMs { get; private set; }
    public double TotalMs { get; private set; }

    private static int BucketOf(double ms)
    {
        if (ms <= MinMs) return 0;
        int b = (int)Math.Floor(Math.Log2(ms / MinMs) * PerOctave);
        return Math.Clamp(b, 0, Buckets - 1);
    }

    private static double UpperOf(int bucket) => MinMs * Math.Pow(2, (bucket + 1) / (double)PerOctave);

    /// <summary>1件足す。遅かったら真を返す（判定はこの1件を足す前の中央値で行う）。</summary>
    public bool Add(double ms)
    {
        bool slow = Count >= WarmUp && ms >= FloorMs && ms >= Median * Factor;
        _hist[BucketOf(ms)]++;
        Count++;
        TotalMs += ms;
        if (ms > MaxMs) MaxMs = ms;
        return slow;
    }

    public double Median => Percentile(0.5);

    /// <summary>度数分布から読む分位点。値は区間の上端なので、実際よりわずかに大きめに出る。</summary>
    public double Percentile(double p)
    {
        if (Count == 0) return 0;
        long target = (long)Math.Ceiling(Count * Math.Clamp(p, 0, 1));
        if (target < 1) target = 1;
        long seen = 0;
        for (int i = 0; i < Buckets; i++)
        {
            seen += _hist[i];
            if (seen >= target) return UpperOf(i);
        }
        return MaxMs;
    }
}
