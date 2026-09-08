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
"""
import json, re, sys, unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / "story-work"
OUT = ROOT / "assets/story.json"

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
        add = [norm_scene(s, f"ins {ins['chapter']}") for s in ins["scenes"]]
        ch["scenes"][at:at] = [a for a in add if a]

# ---- 第二部 ----------------------------------------------------------------
for name in ("part2-a.json", "part2-b.json"):
    d = load(name)
    if not d:
        continue
    for ch in d["chapters"]:
        chapters.append({
            "id": ch["id"], "title": ch["title"],
            "scenes": [s for s in (norm_scene(x, f"{ch['id']}#{i}")
                                   for i, x in enumerate(ch["scenes"])) if s],
        })

# ---- 傍白 ------------------------------------------------------------------
d = load("events.json")
if d:
    for e in d["events"]:
        kind = e["when"]
        slot = events.setdefault(kind, {"when": kind, "cooldown": 900, "texts": []})
        for i, t in enumerate(e["texts"]):
            check_line(t, f"event {kind}#{i}")
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
else:
    print("\n（--write を付けると書き込みます）")
