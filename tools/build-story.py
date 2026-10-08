#!/usr/bin/env python3
"""
story-work/ の原稿を検査して assets/story.json に組み上げる。

**書かせたものをそのまま入れない。** 規約違反は機械で弾く:
  - 日本語か英語が欠けている
  - 条件式が語彙の外（綴り違いは黙って偽になり、二度と出ない文になる）
  - variants に既定（when 無し）が無い
  - 長すぎて画面の2行に収まらない

require / scenePer はここで割り当てる。書き手に配分まで持たせると、
話を足すたびに全体の進み方が壊れる。

**何度流しても同じ物ができる。** 土台は既に組み上がった assets/story.json なので、
挿し込み・第二部・傍白は「もう入っていれば入れない」ようにしてある。
（以前は流すたびに第二部と挿し込みが二重に入った）

冒険の原稿（story-work/adventure-*.json）は assets/adventure.json に組み上げる。
"""
import json, re, sys, unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / "story-work"
OUT = ROOT / "assets/story.json"
ADV_OUT = ROOT / "assets/adventure.json"

CHARS = {"cpu", "cpu-cooler", "memory", "gpu", "m2", "ssd", "hdd", "psu", "motherboard"}
SCALARS = {"chapter", "gpus", "cpus", "boots", "hours"}
OPS = (">=", "<=", "==", ">", "<")
MAX_JA = 70          # 画面の2行。実測で 60字強までしか入らない
problems, warnings = [], []


def width(s: str) -> int:
    """全角を2、半角を1で数える。字数ではなく見た目の幅で切れるため。"""
    return sum(2 if unicodedata.east_asian_width(c) in "WFA" else 1 for c in s)


def check_when(w, where):
    if w in (None, ""):
        return
    if not isinstance(w, str):
        problems.append(f"{where}: when が文字列でない: {w!r}")
        return
    w = w.strip()
    if w.startswith("has:"):
        if w[4:].strip() not in CHARS:
            problems.append(f"{where}: has: の相手が不明 → {w}")
        return
    for op in OPS:
        at = w.find(op)
        if at > 0:
            left, right = w[:at].strip(), w[at + len(op):].strip()
            if left.startswith("level:"):
                if left[6:].strip() not in CHARS:
                    problems.append(f"{where}: level: の相手が不明 → {w}")
            elif left not in SCALARS:
                problems.append(f"{where}: 左辺が不明 → {w}")
            try:
                float(right)
            except ValueError:
                problems.append(f"{where}: 右辺が数でない → {w}")
            return
    problems.append(f"{where}: 演算子が無い → {w}")


def check_line(o, where, need_text=True):
    if not isinstance(o, dict):
        problems.append(f"{where}: 物ではない")
        return
    for k in ("ja", "en"):
        v = o.get(k)
        if need_text and (not isinstance(v, str) or not v.strip()):
            problems.append(f"{where}: {k} が空")
    check_when(o.get("when"), where)
    ja = o.get("ja") or ""
    if width(ja) > MAX_JA * 2:
        warnings.append(f"{where}: 長い({width(ja)//2}字相当) {ja[:28]}…")


def norm_scene(sc, where):
    """場面を variants の形に揃える。"""
    if "variants" in sc:
        vs = sc["variants"]
        if not vs:
            problems.append(f"{where}: variants が空")
            return None
        for i, v in enumerate(vs):
            check_line(v, f"{where}[{i}]")
        if any(v.get("when") for v in vs) and vs[-1].get("when"):
            problems.append(f"{where}: 既定(when 無し)が最後に無い")
        return {"variants": vs}
    check_line(sc, where)
    return dict(sc)


def default_ja(sc):
    """場面の既定の文（分岐なら最後の候補）。同じ場面かどうかの目印にする。"""
    return (sc["variants"][-1] if "variants" in sc else sc).get("ja")


def load(name):
    p = WORK / name
    if not p.exists():
        warnings.append(f"{name} が見つかりません（未着）")
        return None
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except Exception as e:
        problems.append(f"{name}: 読めません {e}")
        return None


# ---- 第一部（既存）を土台にする -------------------------------------------
base = json.loads(OUT.read_text(encoding="utf-8"))
chapters = []
for ch in base["chapters"]:
    chapters.append({
        "id": ch["id"], "title": ch["title"],
        "scenes": [norm_scene(s, f"{ch['id']}#{i}") for i, s in enumerate(ch["scenes"])],
    })
events = {e["when"]: {"when": e["when"], "cooldown": e.get("cooldown", 900), "texts": list(e["texts"])}
          for e in base["events"]}
vignettes = []

# ---- 第一部への挿し込みと分岐 ---------------------------------------------
d = load("part1-branches.json")
if d:
    by_id = {c["id"]: c for c in chapters}
    for b in d.get("branchify", []):
        ch = by_id.get(b["chapter"])
        if not ch:
            problems.append(f"branchify: 章が無い {b['chapter']}")
            continue
        i = b["index"]
        want = [v.get("ja") for v in b["variants"]]
        if any("variants" in sc and [v.get("ja") for v in sc["variants"][:-1]] == want
               for sc in ch["scenes"]):
            continue                                   # もう入っている
        if not (0 <= i < len(ch["scenes"])):
            problems.append(f"branchify {b['chapter']}#{i}: 範囲外")
            continue
        cur = ch["scenes"][i]
        original = cur["variants"][-1] if "variants" in cur else {k: cur[k] for k in ("ja", "en")}
        new = []
        for v in b["variants"]:
            check_line(v, f"branchify {b['chapter']}#{i}")
            new.append(v)
        new.append({k: original[k] for k in ("ja", "en")})     # 元の文を既定に残す
        ch["scenes"][i] = {"variants": new}
    # 後ろから入れる。前から入れると添字がずれる
    for ins in sorted(d.get("insertions", []), key=lambda x: -x["after"]):
        ch = by_id.get(ins["chapter"])
        if not ch:
            problems.append(f"insertions: 章が無い {ins['chapter']}")
            continue
        at = max(0, min(ins["after"] + 1, len(ch["scenes"])))
        add = [a for a in (norm_scene(s, f"ins {ins['chapter']}") for s in ins["scenes"]) if a]
        have = {default_ja(sc) for sc in ch["scenes"]}
        if add and all(default_ja(a) in have for a in add):
            continue                                   # もう入っている
        ch["scenes"][at:at] = add

# ---- 第二部 ----------------------------------------------------------------
for name in ("part2-a.json", "part2-b.json"):
    d = load(name)
    if not d:
        continue
    for ch in d["chapters"]:
        built = {
            "id": ch["id"], "title": ch["title"],
            "scenes": [s for s in (norm_scene(x, f"{ch['id']}#{i}")
                                   for i, x in enumerate(ch["scenes"])) if s],
        }
        # 原稿が正。既に入っている章は原稿の中身で置き換える
        at = next((k for k, c in enumerate(chapters) if c["id"] == ch["id"]), -1)
        if at >= 0:
            chapters[at] = built
        else:
            chapters.append(built)

# ---- 傍白 ------------------------------------------------------------------
d = load("events.json")
if d:
    for e in d["events"]:
        kind = e["when"]
        slot = events.setdefault(kind, {"when": kind, "cooldown": 900, "texts": []})
        at = {t.get("ja"): k for k, t in enumerate(slot["texts"])}
        for i, t in enumerate(e["texts"]):
            check_line(t, f"event {kind}#{i}")
            if t.get("ja") in at:
                # 原稿が正。既に入っている文でも、条件や英語を直したら組み上がりに効かせる
                # （以前は日本語が同じなら飛ばしていたので、原稿に足した when が黙って効かなかった）
                slot["texts"][at[t.get("ja")]] = t
            else:
                slot["texts"].append(t)
        # 条件の付いていない本文が無いと、条件を外れた機械では一度も出ない
        if not any(not t.get("when") for t in slot["texts"]):
            problems.append(f"event {kind}: 条件無しの本文が1つも無い")

# ---- 小話 ------------------------------------------------------------------
for name in ("vignettes-calm.json", "vignettes-busy.json"):
    d = load(name)
    if not d:
        continue
    for i, v in enumerate(d["vignettes"]):
        check_line(v, f"{name}#{i}")
        vignettes.append(v)

# ---- 旅程の割り当て --------------------------------------------------------
# **第一部の進み方は元のまま保つ。** 場面は増えたので、そのぶん長くなるだけ。
# ここを計算式で置き換えると、既に読んでいる人の体感が変わってしまう。
# 第二部だけ、緩やかに間隔を広げながら割り当てる。
PART1_PER = {
    "prologue": 14, "on-the-board": 26, "scorching-valley": 48, "too-much-baggage": 70,
    "long-night": 96, "the-gate": 120, "the-guest": 150, "old-map": 190,
    "overflow": 240, "quiet-day": 300, "beyond": 380,
}
require, out, part2 = 0.0, [], 0
for ch in chapters:
    n = max(1, len(ch["scenes"]))
    if ch["id"] in PART1_PER:
        per = PART1_PER[ch["id"]]
    else:
        per = round(430 * (1.07 ** part2))
        part2 += 1
    ch["require"], ch["scenePer"] = round(require), per
    require += n * per
    out.append(ch)

story = {
    "version": 2,
    "_note": "物語の本文。tools/build-story.py が story-work/ から組み上げる。"
             "手で書き換えてもよいが、その場合は story-work/ 側も直すこと。",
    "chapters": out,
    "events": list(events.values()),
    "vignettes": vignettes,
}

# ---- 冒険 ------------------------------------------------------------------
# 町・行き先・道具・文。数値（強さ・値段）は書かせない。PartsLife.Core が格と得意から決める。
STATS = {"atk", "spd", "cap", "sup", "mag", "bst", "end", "lot"}
SLOTS = {"hand", "body", "charm"}
KINDS = {"field", "dungeon", "side"}
HOLES = {"town", "place", "item", "name", "fighter", "keeper", "finder", "carrier"}
# 種類ごとに使える差し込み。ここに無い差し込みは、その場面では埋まらない
CATEGORY_HOLES = {
    "why-": set(),
    "do-": {"place", "fighter", "keeper", "finder", "carrier"},
    "board-": {"place", "fighter", "keeper", "finder", "carrier"},
    "won-item": {"item", "place", "fighter", "keeper", "finder", "carrier"},
    "side-found": {"item", "place", "fighter", "keeper", "finder", "carrier"},
    "equip": {"name", "item", "town", "fighter", "keeper", "finder", "carrier"},
    "buy": {"name", "item", "town", "fighter", "keeper", "finder", "carrier"},
    "town-": {"town", "fighter", "keeper", "finder", "carrier"},
    "inn-": {"town", "fighter", "keeper", "finder", "carrier"},
    "sell": {"town", "fighter", "keeper", "finder", "carrier"},
    "depart": {"town", "place", "fighter", "keeper", "finder", "carrier"},
    "": {"place", "fighter", "keeper", "finder", "carrier"},
}
REASONS = ["none", "fresh", "rested", "gear-strong", "streak-up", "calm",
           "hurt", "tired", "gear-weak", "heavy", "streak-down", "hot"]
REQUIRED = ([f"why-{r}" for r in REASONS]
            + ["do-push", "do-back", "do-fight", "do-avoid", "do-side", "do-skip", "do-deeper",
               "board-bold", "board-careful", "board-balanced",
               "won", "won-item", "lost", "avoided", "side-found", "side-hurt", "side-small",
               "reach-end", "retreat", "walk-back",
               "town-first", "town-arrive", "town-new", "equip", "sell", "buy", "inn-rest", "inn-eaves", "depart"])
HALF = 34      # 理由と行動は繋げて一文にするので、片方ずつは画面の半分に収める
NAME_JA = "マザーボードくん"   # 差し込まれる名前の最長（幅の見積もり用）


def allowed_holes(cat):
    for prefix, holes in CATEGORY_HOLES.items():
        if prefix and (cat == prefix or (prefix.endswith("-") and cat.startswith(prefix))):
            return holes
    return CATEGORY_HOLES[""]


def holes_of(text):
    return set(re.findall(r"\{([a-z]+)\}", text or ""))


adventure = None
aw, al = load("adventure-world.json"), load("adventure-lines.json")
if aw and al:
    towns, items, place_ids = aw.get("towns", []), aw.get("items", []), set()
    longest_place = max((width(p["name"]["ja"]) for t in towns for p in t.get("places", [])), default=0)
    longest_town = max((width(t["name"]["ja"]) for t in towns), default=0)
    longest_item = max((width(i["name"]["ja"]) for i in items), default=0)
    fill = {"town": longest_town, "place": longest_place, "item": longest_item,
            "name": width(NAME_JA), "fighter": width(NAME_JA), "keeper": width(NAME_JA),
            "finder": width(NAME_JA), "carrier": width(NAME_JA)}

    last = -1
    seen_towns = set()
    for t in towns:
        w = f"town {t.get('id')}"
        if t.get("id") in seen_towns:
            problems.append(f"{w}: id が重複")
        seen_towns.add(t.get("id"))
        check_line(t.get("name", {}), w)
        if not isinstance(t.get("fromChapter"), int) or t["fromChapter"] < last:
            problems.append(f"{w}: fromChapter は章の順に並べること")
        last = t.get("fromChapter", last)
        if t["fromChapter"] >= len(out):
            problems.append(f"{w}: fromChapter {t['fromChapter']} の章が無い")
        if not isinstance(t.get("tier"), int) or t["tier"] < 1:
            problems.append(f"{w}: tier は 1 以上の整数")
        if not t.get("places"):
            problems.append(f"{w}: 行き先が無い")
        for p in t.get("places", []):
            pw = f"{w}/{p.get('id')}"
            if p.get("id") in place_ids:
                problems.append(f"{pw}: id が重複")
            place_ids.add(p.get("id"))
            check_line(p.get("name", {}), pw)
            if p.get("kind") not in KINDS:
                problems.append(f"{pw}: kind は {sorted(KINDS)} のどれか")
            if not (2 <= p.get("length", 4) <= 8):
                problems.append(f"{pw}: length は 2..8")
            if p.get("harder", 0) not in (0, 1):
                problems.append(f"{pw}: harder は 0 か 1")
    if towns and towns[0].get("fromChapter") != 0:
        problems.append("最初の町は fromChapter 0 にすること（序章で泊まる町が無くなる）")

    item_ids = set()
    for it in items:
        w = f"item {it.get('id')}"
        if it.get("id") in item_ids:
            problems.append(f"{w}: id が重複")
        item_ids.add(it.get("id"))
        check_line(it.get("name", {}), w)
        if it.get("slot") not in SLOTS:
            problems.append(f"{w}: slot は {sorted(SLOTS)} のどれか")
        if not isinstance(it.get("tier"), int) or not (0 <= it["tier"] <= 12):
            problems.append(f"{w}: tier は 0..12 の整数")
        if it.get("primary") not in STATS:
            problems.append(f"{w}: primary が不明 {it.get('primary')}")
        if it.get("secondary") is not None and it.get("secondary") not in STATS:
            problems.append(f"{w}: secondary が不明 {it.get('secondary')}")
        for f in it.get("fits") or []:
            if f not in CHARS:
                problems.append(f"{w}: fits の相手が不明 {f}")
    for slot in sorted(SLOTS):
        if not any(i.get("starter") and i.get("slot") == slot for i in items):
            problems.append(f"ありあわせ(starter)の {slot} が無い（初めの装備が配れない）")
    top = max((t.get("tier", 1) for t in towns), default=1)
    for tier in range(1, top + 1):
        for slot in sorted(SLOTS):
            if not any(i.get("tier") in (tier, tier - 1) and i.get("slot") == slot
                       and i.get("shop", True) and not i.get("starter") for i in items):
                warnings.append(f"格 {tier} の町の店に {slot} の品が無い")

    lines_in = al.get("lines", {})
    for cat in REQUIRED:
        if cat not in lines_in:
            problems.append(f"冒険の文: {cat} が無い")
    for cat, arr in lines_in.items():
        holes = allowed_holes(cat)
        if not arr:
            problems.append(f"冒険の文 {cat}: 空")
            continue
        if not any(not x.get("when") for x in arr):
            problems.append(f"冒険の文 {cat}: 条件無しの文が1つも無い")
        limit = HALF if cat.startswith(("why-", "do-", "board-")) else MAX_JA
        for i, x in enumerate(arr):
            w = f"冒険の文 {cat}#{i}"
            check_line(x, w)
            hj, he = holes_of(x.get("ja")), holes_of(x.get("en"))
            if hj != he:
                problems.append(f"{w}: 日本語と英語で差し込みが違う {sorted(hj)} / {sorted(he)}")
            for h in hj | he:
                if h not in HOLES or h not in holes:
                    problems.append(f"{w}: この種類では使えない差し込み {{{h}}}")
            ja = x.get("ja") or ""
            est = width(re.sub(r"\{[a-z]+\}", "", ja)) + sum(fill.get(h, 0) for h in re.findall(r"\{([a-z]+)\}", ja))
            if est > limit * 2:
                (problems if cat.startswith(("why-", "do-", "board-")) else warnings).append(
                    f"{w}: 長い（差し込み込みで {est // 2}字相当、上限 {limit}）{ja[:24]}…")

    adventure = {
        "version": 1,
        "_note": "冒険の本文と表。tools/build-story.py が story-work/adventure-*.json から組み上げる。",
        "towns": towns,
        "items": items,
        "lines": lines_in,
    }
    adv_lines = sum(len(v) for v in lines_in.values())
    print(f"冒険: 町 {len(towns)} / 行き先 {len(place_ids)} / 道具 {len(items)} / 文 {adv_lines}（{len(lines_in)} 種）")

lines = sum(len(s.get("variants", [s])) for c in out for s in c["scenes"]) \
        + sum(len(e["texts"]) for e in events.values()) + len(vignettes)

print(f"章 {len(out)} / 場面 {sum(len(c['scenes']) for c in out)}"
      f" / 傍白 {sum(len(e['texts']) for e in events.values())} / 小話 {len(vignettes)}")
print(f"文章の総数 {lines}")
print(f"最後の章に入る旅程 {out[-1]['require']:,} / 読み切り {require:,.0f}")
if warnings:
    print(f"\n注意 {len(warnings)} 件:")
    for w in warnings[:12]:
        print("  ", w)
if problems:
    print(f"\n**規約違反 {len(problems)} 件。取り込みを中止します。**")
    for p in problems[:30]:
        print("  ", p)
    sys.exit(1)

if "--write" in sys.argv:
    OUT.write_text(json.dumps(story, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n{OUT} に書きました")
    if adventure is not None:
        ADV_OUT.write_text(json.dumps(adventure, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{ADV_OUT} に書きました")
else:
    print("\n（--write を付けると書き込みます）")
