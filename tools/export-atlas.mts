/**
 * ドット絵を1枚のPNG（アトラス）と座標表(JSON)に書き出す。
 *
 * ■なぜこうするか
 * 絵の正は TypeScript 側（`sprites/`）にある。サイトも同じコードで描いている。
 * C# 側に同じ描画コードを持たせると**必ず二重管理になってズレる**ので、
 * ここで焼いた PNG を C# は「切り出して置くだけ」にする。
 * キャラを直したら、この書き出しをやり直せばアプリにも反映される。
 *
 *   npx tsx tools/export-atlas.mts [出力先ディレクトリ]
 */
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { MASCOT_PALETTE } from "../sprites/palette.ts";
import { renderPose, SPRITE_SIZE } from "../sprites/sprite.ts";
import { BODIES } from "../sprites/bodies.ts";
import { DEFAULT_ANCHOR, MOTIONS } from "../sprites/motions.ts";
import { MARK_SIZE, markGrids, markIds } from "../sprites/marks.ts";
import { CHARACTERS } from "../sprites/characters.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const OUT = process.argv[2] ?? join(HERE, "..", "assets");

// ---------------------------------------------------------------------------
// PNG（RGBA・8bit）を書く。透過が要るので色タイプ6を使う。
// ---------------------------------------------------------------------------
function crc32(buf: Buffer): number {
  let c: number;
  let crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = c ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function writePngRgba(path: string, w: number, h: number, rgba: Buffer) {
  const stride = w * 4;
  const raw = Buffer.alloc((stride + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (stride + 1)] = 0; // フィルタなし
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  writeFileSync(path, Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]));
}

// パレット添字 → RGBA。0 は完全透過。
const RGBA = MASCOT_PALETTE.map((hex) => {
  if (hex === "transparent") return [0, 0, 0, 0] as const;
  return [
    parseInt(hex.slice(1, 3), 16),
    parseInt(hex.slice(3, 5), 16),
    parseInt(hex.slice(5, 7), 16),
    255,
  ] as const;
});

// ---------------------------------------------------------------------------
// 詰め込み
// ---------------------------------------------------------------------------
type Cell = { grid: Uint8Array; size: number };

/**
 * 升目に順番に詰める。**枠は取らない。**
 * C# 側は最近傍で等倍に置くだけなので、隣の絵が滲むことはない。
 */
function pack(cells: Cell[], cellSize: number, cols: number) {
  const rows = Math.ceil(cells.length / cols);
  const w = cols * cellSize;
  const h = rows * cellSize;
  const rgba = Buffer.alloc(w * h * 4); // 既定は全部 0 = 透過
  const coords: [number, number][] = [];

  cells.forEach((cell, i) => {
    const cx = (i % cols) * cellSize;
    const cy = Math.floor(i / cols) * cellSize;
    coords.push([cx, cy]);
    for (let y = 0; y < cell.size; y++) {
      for (let x = 0; x < cell.size; x++) {
        const [r, g, b, a] = RGBA[cell.grid[y * cell.size + x]] ?? RGBA[0];
        const p = ((cy + y) * w + (cx + x)) * 4;
        rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = a;
      }
    }
  });

  return { w, h, rgba, coords };
}

// ---------------------------------------------------------------------------
// 本体
// ---------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });

const charCells: Cell[] = [];
const charIndex: { id: string; motion: string; at: number }[] = [];

const characterIds = Object.keys(BODIES);
for (const id of characterIds) {
  for (const [motionId, motion] of Object.entries(MOTIONS)) {
    charIndex.push({ id, motion: motionId, at: charCells.length });
    for (const pose of motion.frames) {
      charCells.push({ grid: renderPose(pose, BODIES[id]), size: SPRITE_SIZE });
    }
  }
}

const CHAR_COLS = 16;
const chars = pack(charCells, SPRITE_SIZE, CHAR_COLS);
writePngRgba(join(OUT, "characters.png"), chars.w, chars.h, chars.rgba);

const markCells: Cell[] = [];
const markIndex: { id: string; at: number; count: number }[] = [];
for (const id of markIds()) {
  const grids = markGrids(id);
  markIndex.push({ id, at: markCells.length, count: grids.length });
  for (const g of grids) markCells.push({ grid: g, size: MARK_SIZE });
}
const marks = pack(markCells, MARK_SIZE, 8);
writePngRgba(join(OUT, "marks.png"), marks.w, marks.h, marks.rgba);

// ---- 座標表 -----------------------------------------------------------------
type MotionOut = {
  ms: number;
  anchor: number;
  once: boolean;
  rope: number | null;
  mark: string | null;
  frames: [number, number][];
};

const out = {
  /** この書き出しの形式。C# 側と食い違ったら弾くために持つ */
  version: 1,
  cell: SPRITE_SIZE,
  markCell: MARK_SIZE,
  characters: {} as Record<string, {
    name: { ja: string; en: string };
    motif: { ja: string; en: string };
    trait: { ja: string; en: string };
    why: { ja: string; en: string };
    disclaimer: { ja: string; en: string };
    family: string;
    motions: Record<string, MotionOut>;
  }>,
  marks: {} as Record<string, { frames: [number, number][] }>,
};

for (const id of characterIds) {
  const meta = CHARACTERS.find((c) => c.id === id);
  const motions: Record<string, MotionOut> = {};
  for (const [motionId, motion] of Object.entries(MOTIONS)) {
    const entry = charIndex.find((e) => e.id === id && e.motion === motionId)!;
    motions[motionId] = {
      ms: motion.frameMs,
      anchor: motion.anchor ?? DEFAULT_ANCHOR,
      once: motion.once ?? false,
      rope: motion.rope ?? null,
      mark: motion.mark ?? null,
      frames: motion.frames.map((_, i) => chars.coords[entry.at + i]),
    };
  }
  out.characters[id] = {
    name: meta?.name ?? { ja: id, en: id },
    motif: meta?.motif ?? { ja: "", en: "" },
    trait: meta?.trait ?? { ja: "", en: "" },
    why: meta?.why ?? { ja: "", en: "" },
    disclaimer: meta?.disclaimer ?? { ja: "", en: "" },
    family: meta?.family ?? "pc",
    motions,
  };
}

for (const m of markIndex) {
  out.marks[m.id] = {
    frames: Array.from({ length: m.count }, (_, i) => marks.coords[m.at + i]),
  };
}

writeFileSync(join(OUT, "atlas.json"), JSON.stringify(out, null, 2) + "\n");

console.log(`characters.png  ${chars.w}x${chars.h}  (${charCells.length}コマ / ${characterIds.length}体)`);
console.log(`marks.png       ${marks.w}x${marks.h}  (${markCells.length}コマ)`);
console.log(`atlas.json      ${Object.keys(out.characters).length}体ぶんの座標と設定`);
