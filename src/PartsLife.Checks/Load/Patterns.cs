namespace PartsLife.Checks.Load;

public enum PatternKind
{
    /// <summary>番地そのもの（を seed で崩した物）。番地線の故障や、別の場所に書けてしまう故障に強い。</summary>
    Address,
    /// <summary>上の反転。全ビットが 0 と 1 の両方を経験する。</summary>
    AddressInverted,
    /// <summary>0101… と 1010… の市松。隣り合うビットの干渉に強い。</summary>
    Checker,
    /// <summary>番地から作る乱数。どの番地でも値が予測できるので、順番に依存しない。</summary>
    Random,
}

/// <summary>1か所の食い違い。</summary>
public sealed record Mismatch(long Index, ulong Expected, ulong Actual)
{
    /// <summary>食い違ったビットの数。1 ビットだけなら典型的なメモリの化け。</summary>
    public int FlippedBits => System.Numerics.BitOperations.PopCount(Expected ^ Actual);
}

public sealed record VerifyResult(long Mismatches, IReadOnlyList<Mismatch> First);

/// <summary>
/// メモリ検査の書き込みと照合。**純粋な関数。**
/// どの値も「番地（何番目の 8 バイトか）と seed」だけから決まるので、
/// 書いた順番やスレッドの割り振りに関係なく、照合の側が期待値を作り直せる。
/// </summary>
public static class Patterns
{
    public static ulong ValueAt(PatternKind kind, ulong seed, long index) => kind switch
    {
        PatternKind.Address => (ulong)index ^ seed,
        PatternKind.AddressInverted => ~((ulong)index ^ seed),
        PatternKind.Checker => ((index & 1) == 0 ? 0x5555555555555555UL : 0xAAAAAAAAAAAAAAAAUL) ^ ((seed & 1) == 0 ? 0 : ulong.MaxValue),
        _ => SplitMix((ulong)index + seed),
    };

    private static ulong SplitMix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary><paramref name="baseIndex"/> は span の先頭が全体の何番目か。</summary>
    public static void Fill(Span<ulong> span, PatternKind kind, ulong seed, long baseIndex)
    {
        switch (kind)
        {
            case PatternKind.Address:
                for (int i = 0; i < span.Length; i++) span[i] = (ulong)(baseIndex + i) ^ seed;
                break;
            case PatternKind.AddressInverted:
                for (int i = 0; i < span.Length; i++) span[i] = ~((ulong)(baseIndex + i) ^ seed);
                break;
            default:
                for (int i = 0; i < span.Length; i++) span[i] = ValueAt(kind, seed, baseIndex + i);
                break;
        }
    }

    /// <summary>照合。食い違いは全部数え、最初の <paramref name="keep"/> 件だけ覚える。</summary>
    public static VerifyResult Verify(ReadOnlySpan<ulong> span, PatternKind kind, ulong seed, long baseIndex, int keep = 8)
    {
        long bad = 0;
        List<Mismatch>? first = null;
        for (int i = 0; i < span.Length; i++)
        {
            ulong want = kind switch
            {
                PatternKind.Address => (ulong)(baseIndex + i) ^ seed,
                PatternKind.AddressInverted => ~((ulong)(baseIndex + i) ^ seed),
                _ => ValueAt(kind, seed, baseIndex + i),
            };
            ulong got = span[i];
            if (got != want)
            {
                bad++;
                if ((first ??= new()).Count < keep) first.Add(new Mismatch(baseIndex + i, want, got));
            }
        }
        return new VerifyResult(bad, (IReadOnlyList<Mismatch>?)first ?? Array.Empty<Mismatch>());
    }
}
