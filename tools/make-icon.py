#!/usr/bin/env python3
"""
exe のアイコンを assets/characters.png から焼き直す。

**アイコンも絵の正（アトラス）から作る。** 別に描き起こすと、
キャラの絵を直したときにアイコンだけ古いまま残る。

なぜ他の道具が TypeScript なのにこれだけ Python なのか:
ICO は複数の大きさを1つの束にした形式で、その符号化を書けるものが
手元では Pillow しか無い。アイコンは滅多に変えないので、
そのためだけに TS 側へ依存を増やすのは割に合わないと判断した。

    python3 tools/make-icon.py      # 要 Pillow
"""
import json
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
d = json.loads((ROOT / "assets/atlas.json").read_text(encoding="utf-8"))
cell = d["cell"]

sheet = Image.open(ROOT / "assets/characters.png").convert("RGBA")
x, y = d["characters"]["cpu"]["motions"]["idle"]["frames"][0]
# 顔が一番はっきり残るのが CPU くん。16px まで落としても目と口が見える
trim = sheet.crop((x, y, x + cell, y + cell)).crop(
    sheet.crop((x, y, x + cell, y + cell)).getbbox())

master = trim.resize((trim.width * 16, trim.height * 16), Image.NEAREST)

def icon(size: int) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pad = 1 if size <= 32 else max(2, size // 16)
    box = size - pad * 2
    n = min(box // trim.width, box // trim.height)
    if n >= 1:
        # 整数倍で入るなら最近傍。ドット絵は補間した瞬間に別物になる
        s = trim.resize((trim.width * n, trim.height * n), Image.NEAREST)
    else:
        # 16px などは整数倍にできない。大きい版から滑らかに落とす
        r = box / max(trim.width, trim.height)
        s = master.resize((max(1, int(trim.width * r)), max(1, int(trim.height * r))),
                          Image.LANCZOS)
    im.paste(s, ((size - s.width) // 2, (size - s.height) // 2), s)
    return im

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [icon(s) for s in SIZES]
out = ROOT / "assets/PartsLife.ico"
imgs[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
print(f"wrote {out} ({', '.join(str(s) for s in SIZES)})")
