namespace PartsLife.Adventure;

/// <summary>一行の一人。鍵は経験値と同じ（gpu#0 等）、CharacterId は役（gpu 等）。</summary>
public readonly record struct Member(string Key, string CharacterId);

/// <summary>
/// 装備の決まり。**選ぶのは本人で、利用者は選べない。**
///
/// 決め方:
///   1. 点数 = Σ 道具の得意 × その役・その構えの重み（整数）
///   2. 一人ずつ（鍵の文字順）、手・身・守りの順に、持ち物の中から一番点の高い物に替える
///   3. **今の物より真に高いときだけ替える。** 同点なら替えない
///   4. 同点の候補どうしは、格の高い方 → id の文字順で先の方
///
/// 3 があるので、装備を替えて点が下がることは無い。4 があるので、持ち物の並び順が
/// 違っても結果は同じになる。
/// </summary>
public static class Equipment
{
    public const int SlotCount = 3;

    public static int ItemScore(ItemDef? item, string characterId, Stance stance)
    {
        if (item is null || !item.FitsCharacter(characterId)) return 0;
        var w = Roles.Weights(characterId, stance);
        int score = 0;
        for (int s = 0; s < Roles.StatCount; s++) score += item.Stats[s] * w[s];
        return score;
    }

    public static int MemberScore(AdventureBook book, AdventureState state, in Member m, Stance stance)
    {
        if (!state.Equipped.TryGetValue(m.Key, out var eq)) return 0;
        int sum = 0;
        for (int i = 0; i < SlotCount; i++) sum += ItemScore(book.Item(eq[i]), m.CharacterId, stance);
        return sum;
    }

    /// <summary>a の方が良い候補か（同点の決め方を含む）。</summary>
    private static bool Better(ItemDef a, int scoreA, ItemDef b, int scoreB)
    {
        if (scoreA != scoreB) return scoreA > scoreB;
        if (a.Tier != b.Tier) return a.Tier > b.Tier;
        return string.CompareOrdinal(a.Id, b.Id) < 0;
    }

    /// <summary>
    /// 持ち物から、一人ひとりに一番良い物を着けさせる。
    /// members は鍵の文字順に並べて渡すこと（<see cref="SortMembers"/>）。
    /// 返り値は替えた数。最初に替えた者と物を返す（物語の一文に使う）。
    /// </summary>
    public static int AutoEquip(AdventureBook book, AdventureState state, IReadOnlyList<Member> members,
                                Stance stance, out Member who, out ItemDef? what)
    {
        who = default;
        what = null;
        int changes = 0;
        var inv = state.Inventory;

        // 後の者が外した物を、前の者が欲しがることがある。替える物が無くなるまで回す。
        // 替えるたびに点の合計が真に増えるので必ず止まるが、念のため回数で切る
        for (int pass = 0; pass < 8; pass++)
        {
            int before = changes;
            changes = Pass(book, state, members, stance, inv, changes, ref who, ref what);
            if (changes == before) break;
        }
        return changes;
    }

    private static int Pass(AdventureBook book, AdventureState state, IReadOnlyList<Member> members,
                            Stance stance, List<string> inv, int changes, ref Member who, ref ItemDef? what)
    {
        for (int mi = 0; mi < members.Count; mi++)
        {
            var m = members[mi];
            if (!state.Equipped.TryGetValue(m.Key, out var eq))
            {
                eq = new[] { "", "", "" };
                state.Equipped[m.Key] = eq;
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                var current = book.Item(eq[slot]);
                int currentScore = ItemScore(current, m.CharacterId, stance);

                int bestIdx = -1, bestScore = currentScore;
                ItemDef? best = null;
                for (int i = 0; i < inv.Count; i++)
                {
                    var cand = book.Item(inv[i]);
                    if (cand is null || (int)cand.SlotValue != slot) continue;
                    int sc = ItemScore(cand, m.CharacterId, stance);
                    if (sc <= currentScore) continue;               // 真に良くなる物だけ
                    if (best is null || Better(cand, sc, best, bestScore))
                    {
                        best = cand; bestScore = sc; bestIdx = i;
                    }
                }
                if (best is null) continue;

                // 外した物は持ち物に戻す。何も着けていなかったなら、持ち物が一つ減る
                if (current is not null) inv[bestIdx] = current.Id;
                else inv.RemoveAt(bestIdx);
                eq[slot] = best.Id;

                if (changes == 0) { who = m; what = best; }
                changes++;
            }
        }
        return changes;
    }

    /// <summary>
    /// その道具が、いまの誰かの装備より真に良くなるか（どの構えでも）。
    /// 良くならない物は町で売ってよい。
    /// </summary>
    public static bool UsefulToAnyone(AdventureBook book, AdventureState state, IReadOnlyList<Member> members, ItemDef item)
    {
        int slot = (int)item.SlotValue;
        for (int mi = 0; mi < members.Count; mi++)
        {
            var m = members[mi];
            if (!item.FitsCharacter(m.CharacterId)) continue;
            state.Equipped.TryGetValue(m.Key, out var eq);
            var current = eq is null ? null : book.Item(eq[slot]);
            for (int st = 0; st < 3; st++)
                if (ItemScore(item, m.CharacterId, (Stance)st) > ItemScore(current, m.CharacterId, (Stance)st))
                    return true;
        }
        return false;
    }

    /// <summary>まだ何も持っていない者に、ありあわせを持たせる（新しく挿さった部品、初めての起動）。</summary>
    public static bool GiveStarters(AdventureBook book, AdventureState state, in Member m)
    {
        if (state.Equipped.ContainsKey(m.Key)) return false;
        var eq = new[] { "", "", "" };
        for (int slot = 0; slot < SlotCount; slot++)
        {
            ItemDef? best = null;
            int bestScore = 0;
            foreach (var it in book.Starters)
            {
                if ((int)it.SlotValue != slot) continue;
                int sc = ItemScore(it, m.CharacterId, Stance.Balanced);
                if (sc <= 0) continue;
                if (best is null || Better(it, sc, best, bestScore)) { best = it; bestScore = sc; }
            }
            if (best is not null) eq[slot] = best.Id;
        }
        state.Equipped[m.Key] = eq;
        return true;
    }

    public static Member[] SortMembers(IEnumerable<Member> members) =>
        members.OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
}
