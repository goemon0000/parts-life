import { C } from "./palette";
import { insideRoundedSquare, put, SPRITE_SIZE, type BodySpec, type Grid } from "./sprite";

/**
 * キャラごとの「体」の描き方。
 *
 * 動き（歩く・座る・ぶら下がる…）と顔・手足の付け方は sprite.ts が全キャラ共通で持つ。
 * ここに書くのは **その部品に見える形** と、顔と手足を付ける位置だけ。
 * キャラを増やすときは、この形式で1件足して characters.ts から参照する。
 *
 * 絵を直したら必ず `npx tsx scripts/mascot-png.mjs <出力先>` で全コマを見ること。
 * 数値だけ見て頭の中で想像しても当たらない（実際に何度も外した）。
 */

const TAU = Math.PI * 2;

/**
 * **明るい長方形を暗い枠で囲むと、それだけで「画面」に見える。**
 * 顔を載せる面を作るときは、画面を持つキャラ（モニター・アーケード・携帯機）
 * 以外は白い板にしない。金属の地色より少し明るい程度に留める。
 * 2026-09-07、電源・SSD・キーボードがモニターに見えると指摘されて分かった。
 */
const FACE_PLATE_NOTE = true;
void FACE_PLATE_NOTE;

// ============================================================
// CPUクーラーくん — ファンが顔
// ============================================================
// --- ファン本体の寸法（すべてこの4つから導く。個別に数字を散らさない）---
const CX = 15.5;           // ファンの中心 x
const CY = 13.5;           // ファンの中心 y
const FRAME_HALF = 12;     // 枠の外周の半幅（24x24 の枠になる）
const FRAME_CORNER = 4;    // 枠の角の丸み
const HOLE_R = 10.2;       // 枠の内側にあいた円の半径
const BLADE_OUT = 9.9;     // 羽根の外周
const BLADE_IN = 5.9;      // 羽根の内周
const HUB_R = 5.6;         // ハブ（＝顔）の半径

const BLADE_COUNT = 7;     // 羽根の枚数。奇数の方が回転が見やすい
const BLADE_CURVE = 0.17;  // 羽根の反り（半径1pxあたりの角度ずれ）
const BLADE_FILL = 0.62;   // 1区画のうち羽根が占める割合

/** ファン枠（角丸の四角 − 中央の円）。四隅にはサイト色のLEDを置く。 */
function drawCoolerFrame(g: Grid) {
  for (let y = 0; y < SPRITE_SIZE; y++) {
    for (let x = 0; x < SPRITE_SIZE; x++) {
      const dx = x + 0.5 - CX;
      const dy = y + 0.5 - CY;
      if (!insideRoundedSquare(dx, dy, FRAME_HALF, FRAME_CORNER)) continue;
      const r = Math.hypot(dx, dy);
      if (r < HOLE_R) continue;
      // 光源は左上。左上側を明るく、右下側を暗くして厚みを出す。
      const lit = dx + dy;
      put(g, x, y, lit < -6 ? C.frameLight : lit > 7 ? C.frameDark : C.frame);
    }
  }
  // 四隅のネジ穴＝LED。サイトのアクセント色を1点だけ使い、サイトとの所属を示す。
  for (const [sx, sy] of [[-1, -1], [1, -1], [-1, 1], [1, 1]] as const) {
    const bx = Math.round(CX + sx * 8.5 - 0.5);
    const by = Math.round(CY + sy * 8.5 - 0.5);
    put(g, bx, by, C.accent);
    put(g, bx + 1, by, C.accent);
    put(g, bx, by + 1, C.accent);
    put(g, bx + 1, by + 1, C.accent);
  }
}

/**
 * 羽根。phase は 0..1 で羽根1枚分の回転にあたる。
 * 4フレームなら 0, 0.25, 0.5, 0.75 で「ちょうど1周期」になり継ぎ目なく回る。
 */
function drawCoolerBlades(g: Grid, phase: number) {
  const sector = TAU / BLADE_COUNT;
  for (let y = 0; y < SPRITE_SIZE; y++) {
    for (let x = 0; x < SPRITE_SIZE; x++) {
      const dx = x + 0.5 - CX;
      const dy = y + 0.5 - CY;
      const r = Math.hypot(dx, dy);
      if (r < BLADE_IN || r > BLADE_OUT) continue;
      // 半径が大きいほど角度をずらす＝反った羽根になる
      const swept = Math.atan2(dy, dx) + (r - BLADE_IN) * BLADE_CURVE + phase * sector;
      const k = ((swept % sector) + sector) % sector;
      const frac = k / sector;
      if (frac > BLADE_FILL) {
        put(g, x, y, C.bladeGap); // 羽根と羽根の間。ここを最も暗くして「穴」に見せる
      } else {
        put(g, x, y, frac < 0.20 ? C.bladeEdge : C.blade); // 前縁だけ明るく＝回転が見える
      }
    }
  }
}

/**
 * ハブ（顔の下地）。
 * **外周1pxを暗くして縁取る。** これが無いと、明るいハブと羽根の前縁が
 * つながって顔が羽根に溶ける（実機の確認で判明）。
 */
const HUB_RIM = 0.85;

function drawCoolerHub(g: Grid) {
  for (let y = 0; y < SPRITE_SIZE; y++) {
    for (let x = 0; x < SPRITE_SIZE; x++) {
      const dx = x + 0.5 - CX;
      const dy = y + 0.5 - CY;
      const r = Math.hypot(dx, dy);
      if (r > HUB_R) continue;
      if (r > HUB_R - HUB_RIM) { put(g, x, y, C.outline); continue; }
      const lit = dx + dy;
      put(g, x, y, lit < -3.5 ? C.hubLight : lit > 4 ? C.hubDark : C.hub);
    }
  }
}


export const COOLER_BODY: BodySpec = {
  draw: (g: Grid, spin: number) => {
    drawCoolerFrame(g);
    drawCoolerBlades(g, spin);
    drawCoolerHub(g);
  },
  faceX: 10,
  faceY: 8,
  armY: 16,
  armLeftX: 1,
  armRightX: 30,
  legY: 26,
  legLeftX: 12,
  legRightX: 18,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 2,
};

// ============================================================
// CPUくん — ヒートスプレッダ（金属の天面）が顔
// ============================================================

const CPU_CX = 15.5;
const CPU_CY = 13.5;
const CPU_SUB_HALF = 11;    // 基板（緑）の半幅
const CPU_SUB_CORNER = 2;
const CPU_IHS_HALF = 8.5;   // 金属天面の半幅。ここが顔になる
const CPU_IHS_CORNER = 3;

function drawCpu(g: Grid) {
  for (let y = 0; y < SPRITE_SIZE; y++) {
    for (let x = 0; x < SPRITE_SIZE; x++) {
      const dx = x + 0.5 - CPU_CX;
      const dy = y + 0.5 - CPU_CY;
      if (!insideRoundedSquare(dx, dy, CPU_SUB_HALF, CPU_SUB_CORNER)) continue;
      if (insideRoundedSquare(dx, dy, CPU_IHS_HALF, CPU_IHS_CORNER)) {
        // 金属の天面。光源は左上
        const lit = dx + dy;
        put(g, x, y, lit < -6 ? C.frameLight : lit > 7 ? C.frameDark : C.frame);
      } else {
        // 基板。外周を1px濃くして厚みを出す
        const edge = Math.max(Math.abs(dx), Math.abs(dy)) > CPU_SUB_HALF - 1.2;
        put(g, x, y, edge ? C.pcb : C.pcbLight);
      }
    }
  }
  // 1番ピンの三角印（左下）。CPUと分かる一番小さな手がかり
  const bx = Math.round(CPU_CX - CPU_SUB_HALF + 1);
  const by = Math.round(CPU_CY + CPU_SUB_HALF - 3);
  put(g, bx, by + 2, C.gold);
  put(g, bx + 1, by + 2, C.gold);
  put(g, bx, by + 1, C.gold);
  // 天面の刻印に見える線（顔の上、目に被らない位置）
  for (let x = 11; x <= 20; x++) put(g, x, 7, C.frameDark);
}

export const CPU_BODY: BodySpec = {
  draw: (g: Grid) => drawCpu(g),
  faceX: 10,
  faceY: 9,
  armY: 16,
  armLeftX: 1,
  armRightX: 30,
  legY: 25,
  legLeftX: 12,
  legRightX: 18,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 3,
};

// ============================================================
// GPUくん — 大きなファンが顔。派手なので上に光の帯を入れる
// ============================================================

// 横長の板であることがGPUの一番の手がかり。正方形にしない
const GPU_L = 2, GPU_R = 29, GPU_T = 7, GPU_B = 25;
// 左の大きなファンが顔。中心と、顔を載せる面の半径
const GPU_FAN_CX = 11.5, GPU_FAN_CY = 16, GPU_FAN_R = 7.2, GPU_HUB_R = 5.2;

function drawGpu(g: Grid, spin: number) {
  for (let y = GPU_T; y <= GPU_B; y++) {
    for (let x = GPU_L; x <= GPU_R; x++) {
      // 角を少し落とす
      const cx = x < GPU_L + 2 ? GPU_L + 2 - x : x > GPU_R - 2 ? x - (GPU_R - 2) : 0;
      const cy = y < GPU_T + 2 ? GPU_T + 2 - y : y > GPU_B - 2 ? y - (GPU_B - 2) : 0;
      if (cx + cy > 2) continue;
      put(g, x, y, y < GPU_T + 3 ? C.frame : C.bladeGap);
    }
  }
  // 天面の光る帯。派手・自信家という性格をここで出す
  for (let x = GPU_L + 3; x <= GPU_R - 3; x++) put(g, x, GPU_T + 1, C.accent);

  // 顔になる大きなファンと、右の小さなファン
  drawFanDisc(g, GPU_FAN_CX, GPU_FAN_CY, GPU_FAN_R, spin, 5);
  drawFanDisc(g, 23.5, GPU_FAN_CY, 4.2, spin + 0.5, 5);
  // **顔を載せる明るい面（ハブ）。** これが無いと、暗い羽根の上に
  // 暗い目を描くことになって顔が消える（実機の確認で判明）。
  // 半径は顔グリッド(12x12)の目と口が収まる大きさが要る。小さいと顔が欠ける。
  drawDisc(g, GPU_FAN_CX, GPU_FAN_CY, GPU_HUB_R);
}

/**
 * 顔を載せる明るい円盤。**暗い体のキャラには必ずこれを置く。**
 * 目と口はインク（暗色）なので、下地が暗いと顔が読めなくなる。
 */
function drawDisc(g: Grid, cx: number, cy: number, r: number) {
  for (let y = Math.floor(cy - r); y <= Math.ceil(cy + r); y++) {
    for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++) {
      const dx = x + 0.5 - cx;
      const dy = y + 0.5 - cy;
      const d = Math.hypot(dx, dy);
      if (d > r) continue;
      if (d > r - 0.85) { put(g, x, y, C.outline); continue; }
      const lit = dx + dy;
      put(g, x, y, lit < -3 ? C.hubLight : lit > 3.5 ? C.hubDark : C.hub);
    }
  }
}

/** 円盤の羽根。GPUや電源など、ファンを持つキャラで使い回す。 */
function drawFanDisc(g: Grid, cx: number, cy: number, r: number, phase: number, blades: number) {
  const sector = TAU / blades;
  for (let y = Math.floor(cy - r); y <= Math.ceil(cy + r); y++) {
    for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++) {
      const dx = x + 0.5 - cx;
      const dy = y + 0.5 - cy;
      const d = Math.hypot(dx, dy);
      if (d > r) continue;
      if (d < r * 0.34) { put(g, x, y, C.frameDark); continue; } // 軸
      const swept = Math.atan2(dy, dx) + d * 0.2 + phase * sector;
      const k = ((swept % sector) + sector) % sector;
      put(g, x, y, k / sector < 0.55 ? C.blade : C.bladeGap);
    }
  }
}

export const GPU_BODY: BodySpec = {
  draw: (g: Grid, spin: number) => drawGpu(g, spin),
  // 顔グリッドの目はコル3-4/7-8、行3-5。中心をファンの中心に合わせる
  faceX: 6,
  faceY: 11,
  armY: 17,
  armLeftX: 0,
  armRightX: 31,
  legY: 26,
  legLeftX: 10,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 7,
};

// ============================================================
// メモリくん — 縦長の基板。上のヒートスプレッダが顔
// ============================================================

function drawMemory(g: Grid) {
  // **横長にする。** 縦長だとHDDやコンデンサーに見えて、メモリだと分からない
  // （実際にそう指摘された）。細長い板であることが一番の手がかり。
  const L = 3, R = 28, T = 9, B = 22;
  const SPREADER_B = 18; // ここまでがヒートスプレッダ（金属）、下が基板
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      if (y <= SPREADER_B) {
        const lit = (x - 15.5) * 0.4 + (y - 13);
        put(g, x, y, lit < -4 ? C.frameLight : lit > 4 ? C.frameDark : C.frame);
      } else {
        put(g, x, y, C.pcbLight);
      }
    }
  }
  // ヒートスプレッダの上端のギザギザ（放熱フィン）
  for (let x = L + 1; x <= R - 1; x += 3) { put(g, x, T - 1, C.frameLight); put(g, x + 1, T - 1, C.frame); }
  // 金メッキの端子と、DDRの切り欠き
  for (let x = L + 1; x <= R - 1; x++) put(g, x, B, C.gold);
  put(g, 12, B, C.pcb);
  put(g, 13, B, C.pcb);
  // 左右の縁
  for (let y = T; y <= SPREADER_B; y++) { put(g, L, y, C.frameDark); put(g, R, y, C.frameDark); }
}

export const MEMORY_BODY: BodySpec = {
  draw: (g: Grid) => drawMemory(g),
  faceX: 10,
  faceY: 8,
  armY: 14,
  armLeftX: 0,
  armRightX: 31,
  legY: 23,
  legLeftX: 12,
  legRightX: 18,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 8,
};

// ============================================================
// マザーボードくん — 緑の板。ソケット・スロットが体の模様
// ============================================================

function drawMotherboard(g: Grid) {
  const L = 2, R = 29, T = 4, B = 27;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      const edge = x === L || x === R || y === T || y === B;
      put(g, x, y, edge ? C.pcb : C.pcbLight);
    }
  }
  // 取り付け穴（四隅）
  for (const [hx, hy] of [[L + 1, T + 1], [R - 1, T + 1], [L + 1, B - 1], [R - 1, B - 1]] as const) {
    put(g, hx, hy, C.frameLight);
  }
  // メモリスロット（右の縦棒2本）
  for (let y = T + 3; y <= B - 4; y++) { put(g, 25, y, C.frameDark); put(g, 27, y, C.frameDark); }
  // 拡張スロット（下の横棒）
  for (let x = L + 2; x <= 21; x++) put(g, x, B - 2, C.frameDark);
  // CPUソケット＝顔を載せる面
  for (let y = 7; y <= 19; y++) {
    for (let x = 6; x <= 20; x++) {
      const edge = x === 6 || x === 20 || y === 7 || y === 19;
      put(g, x, y, edge ? C.frameDark : C.frame);
    }
  }
}

export const MOTHERBOARD_BODY: BodySpec = {
  draw: (g: Grid) => drawMotherboard(g),
  faceX: 7,
  faceY: 7,
  armY: 16,
  armLeftX: 0,
  armRightX: 31,
  legY: 28,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 4,
};

// ============================================================
// 電源くん — 箱。前面の吸気口と、横から出るケーブル
// ============================================================

function drawPsu(g: Grid) {
  const L = 3, R = 27, T = 6, B = 25;
  // 電源は黒い箱。CPUくん（金属の箱）と見分けが付くよう、はっきり暗くする
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      const lit = (x - 15) + (y - 15);
      put(g, x, y, lit < -10 ? C.frame : lit > 10 ? C.bladeGap : C.frameDark);
    }
  }
  // 吸気の格子（左側）
  for (let y = T + 2; y <= B - 2; y += 2) {
    for (let x = L + 1; x <= L + 3; x++) put(g, x, y, C.bladeGap);
  }
  // 顔を載せる面。
  // **白い板にしてはいけない。** 暗い枠の中に明るい長方形があると
  // モニターの画面に見える（実際にそう指摘された）。
  // 金属の地色より少し明るいだけに留め、縁も付けない。
  for (let y = 9; y <= 21; y++) {
    for (let x = 11; x <= 23; x++) {
      put(g, x, y, (x - 17) + (y - 15) < -6 ? C.frameLight : C.frame);
    }
  }
  // 電源スイッチとACの差し込み口。電源だと分かる手がかりを増やす
  for (let y = 9; y <= 12; y++) for (let x = 24; x <= 26; x++) put(g, x, y, C.bladeGap);
  put(g, 25, 10, C.frameLight);
  put(g, 24, 14, C.frameDark);
  put(g, 25, 14, C.frameDark);
  put(g, 26, 14, C.frameDark);
  // 出力ケーブルの束。SSDくん（同じ灰色の箱）と見分ける一番の手がかりなので太くする
  for (let i = 0; i < 4; i++) {
    put(g, R + 1 + i % 2, 18 + i, C.bladeGap);
    put(g, R + 2 - i % 2, 19 + i, C.frameDark);
  }
  put(g, R + 1, 17, C.bladeGap);
  put(g, R + 2, 17, C.bladeGap);

}

export const PSU_BODY: BodySpec = {
  draw: (g: Grid) => drawPsu(g),
  faceX: 10,
  faceY: 9,
  armY: 17,
  armLeftX: 0,
  armRightX: 30,
  legY: 26,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 6,
};


// ============================================================
// HDDくん — 蓋を開けた状態。円盤（プラッタ）が顔
// ============================================================

function drawHdd(g: Grid) {
  const L = 2, R = 29, T = 5, B = 26;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      const edge = x <= L + 1 || x >= R - 1 || y <= T + 1 || y >= B - 1;
      put(g, x, y, edge ? C.frameDark : C.frame);
    }
  }
  // 四隅のネジ
  for (const [sx, sy] of [[L + 1, T + 1], [R - 1, T + 1], [L + 1, B - 1], [R - 1, B - 1]] as const) {
    put(g, sx, sy, C.bladeGap);
  }
  // 円盤。ここが顔になるので明るくする
  drawDisc(g, 13.5, 15.5, 8.4);
  put(g, 13, 15, C.frameDark);
  put(g, 14, 15, C.frameDark);
  put(g, 13, 16, C.frameDark);
  put(g, 14, 16, C.frameDark);
  // ヘッドのアーム（右下から円盤へ伸びる棒）。HDDだと分かる一番の手がかり
  for (let i = 0; i < 7; i++) put(g, 25 - i, 23 - i, C.frameLight);
  put(g, 25, 24, C.bladeGap);
  put(g, 26, 23, C.bladeGap);
}

export const HDD_BODY: BodySpec = {
  draw: (g: Grid) => drawHdd(g),
  faceX: 8,
  faceY: 11,
  armY: 17,
  armLeftX: 0,
  armRightX: 31,
  legY: 27,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 5,
};

// ============================================================
// SSDくん — 2.5インチの黒い箱。明るいラベルが顔
// ============================================================

function drawSsd(g: Grid) {
  const L = 4, R = 27, T = 8, B = 24;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      const cx = x < L + 1 ? L + 1 - x : x > R - 1 ? x - (R - 1) : 0;
      const cy = y < T + 1 ? T + 1 - y : y > B - 1 ? y - (B - 1) : 0;
      if (cx + cy > 1) continue;
      put(g, x, y, C.frameDark);
    }
  }
  // ラベル＝顔を載せる面。白い板にすると画面に見えるので金属寄りの色にする
  for (let y = 10; y <= 22; y++) {
    for (let x = 8; x <= 23; x++) {
      put(g, x, y, (x - 16) + (y - 16) < -6 ? C.frameLight : C.frame);
    }
  }
  // 型番の帯（ラベルらしさ）
  for (let x = 10; x <= 16; x++) put(g, x, 20, C.frameDark);
  // SATA端子（左側の切り欠き）
  for (let y = 13; y <= 19; y++) put(g, L - 1, y, C.gold);
  put(g, L - 1, 16, C.frameDark);
}

export const SSD_BODY: BodySpec = {
  draw: (g: Grid) => drawSsd(g),
  faceX: 10,
  faceY: 11,
  armY: 16,
  armLeftX: 1,
  armRightX: 30,
  legY: 25,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 8,
};

// ============================================================
// M.2 SSDくん — 横長の基板。左端の金端子と切り欠きが目印
// ============================================================

/**
 * SSDくん（2.5インチ）と見分けが付かないと入れる意味が無い。
 * **基板がむき出しであること**と**左端の金端子に切り欠きがあること**の2つで分ける。
 * 2.5インチは金属の箱で覆われているので、緑が見えれば M.2 だと分かる。
 *
 * メモリくんも横長だが、あちらは全面がヒートスプレッダで金端子は下辺に並ぶ。
 * こちらは端子が**左端**に立っていて、右端に固定ねじの耳がある。
 */
function drawM2(g: Grid) {
  const L = 3, R = 28, T = 12, B = 21;

  // 基板
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) put(g, x, y, (x + y) % 7 === 0 ? C.pcbLight : C.pcb);
  }

  // 左端の金端子。**切り欠き（Mキー）を1本空ける。**これが M.2 の一番の目印
  for (let y = T + 1; y <= B - 1; y++) {
    if (y === 16) continue;               // 切り欠き
    put(g, L - 1, y, C.gold);
    put(g, L, y, C.gold);
  }
  put(g, L - 1, 16, C.frameDark);
  put(g, L, 16, C.frameDark);

  // 中央の大きなチップ＝顔を載せる面。ここだけ明るくして表情が読めるようにする
  for (let y = 13; y <= 20; y++) {
    for (let x = 9; x <= 24; x++) {
      put(g, x, y, (x - 17) + (y - 17) < -6 ? C.frameLight : C.frame);
    }
  }
  // 小さいチップを1つ添えると「基板に部品が載っている」と読める
  for (let y = 14; y <= 16; y++) for (let x = 6; x <= 8; x++) put(g, x, y, C.frameDark);

  // 右端の固定ねじの耳。半円に見えるよう角を落とす
  for (let y = 15; y <= 18; y++) put(g, R, y, C.pcbLight);
  put(g, R, 15, C.pcb);
  put(g, R, 18, C.pcb);
  put(g, R - 1, 16, C.frameDark);
  put(g, R - 1, 17, C.frameDark);
}

export const M2_BODY: BodySpec = {
  draw: (g: Grid) => drawM2(g),
  faceX: 11,
  faceY: 14,
  armY: 17,
  armLeftX: 1,
  armRightX: 30,
  legY: 22,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 12,
};

// ============================================================
// マウスくん — 上から見た形。手前側が顔
// ============================================================

function drawMouse(g: Grid) {
  // 上から見た形。**下ほど広い卵型**。細いと歯や水滴に見える。
  const CXm = 15.5, TOPY = 5, BOTY = 27;
  for (let y = TOPY; y <= BOTY; y++) {
    const t = (y - TOPY) / (BOTY - TOPY);
    // 上は細く、6割あたりで最も太く、下はわずかに絞る。
    // 下へ行くほど太くする形にすると三角形（歯）に見えてマウスに読めない。
    const half = 4.6 + 5.2 * Math.sin(Math.min(1, t / 0.62) * Math.PI * 0.5) - (t > 0.62 ? (t - 0.62) * 2.4 : 0);
    for (let x = Math.round(CXm - half); x <= Math.round(CXm + half); x++) {
      const upper = y <= 13; // ボタン面。ここを明るくして2枚の板に見せる
      const lit = (x - CXm) * 0.7 + (y - 16);
      // 下半分は顔を載せる面。暗くすると目（インク）が消えるので明るめに保つ
      put(g, x, y, upper
        ? (x < CXm ? C.frameLight : C.frame)
        : lit < -6 ? C.frameLight : lit > 8 ? C.frameDark : C.frame);
    }
  }
  // 左右ボタンの割れ目とホイール。マウスだと分かる一番の手がかり
  for (let y = TOPY; y <= 13; y++) { put(g, 15, y, C.bladeGap); put(g, 16, y, C.bladeGap); }
  for (let y = 8; y <= 11; y++) { put(g, 15, y, C.accent); put(g, 16, y, C.accent); }
  // ボタンと本体の境目
  for (let x = 8; x <= 23; x++) put(g, x, 14, C.bladeGap);
  // ケーブル
  put(g, 15, TOPY - 1, C.frameDark);
  put(g, 15, TOPY - 2, C.frameDark);
  put(g, 16, TOPY - 3, C.frameDark);
  put(g, 16, TOPY - 4, C.frameDark);
}

export const MOUSE_BODY: BodySpec = {
  draw: (g: Grid) => drawMouse(g),
  faceX: 10,
  faceY: 11,
  armY: 18,
  armLeftX: 3,
  armRightX: 28,
  legY: 27,
  legLeftX: 12,
  legRightX: 17,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 5,
};

// ============================================================
// キーボードくん — 横長。キーの粒が並ぶ。中央の面が顔
// ============================================================

function drawKeyboard(g: Grid) {
  const L = 1, R = 30, T = 9, B = 24;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      put(g, x, y, y === T ? C.frameLight : y === B ? C.bladeGap : C.frameDark);
    }
  }
  // キーの粒。顔の面（中央）は避けて置く
  for (let ky = T + 2; ky <= B - 2; ky += 3) {
    for (let kx = L + 2; kx <= R - 2; kx += 3) {
      if (kx >= 9 && kx <= 23 && ky >= 11 && ky <= 22) continue;
      put(g, kx, ky, C.frame);
      put(g, kx + 1, ky, C.frame);
    }
  }
  // 顔を載せる面。白い板にすると画面に見えるので、キーの色に寄せる
  for (let y = 11; y <= 22; y++) {
    for (let x = 9; x <= 23; x++) {
      put(g, x, y, (x - 16) + (y - 16) < -6 ? C.frameLight : C.frame);
    }
  }
}

export const KEYBOARD_BODY: BodySpec = {
  draw: (g: Grid) => drawKeyboard(g),
  faceX: 10,
  faceY: 12,
  armY: 17,
  armLeftX: 0,
  armRightX: 31,
  legY: 25,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 9,
};

// ============================================================
// モニターくん — 画面がそのまま顔。表情が一番出るキャラ
// ============================================================

function drawMonitor(g: Grid) {
  const L = 3, R = 28, T = 3, B = 21;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) put(g, x, y, C.frameDark);
  }
  // 画面
  for (let y = T + 2; y <= B - 2; y++) {
    for (let x = L + 2; x <= R - 2; x++) {
      const lit = (x - 15.5) + (y - 12);
      put(g, x, y, lit < -8 ? C.hubLight : lit > 8 ? C.hubDark : C.hub);
    }
  }
  // 首と台座
  for (let y = B + 1; y <= B + 4; y++) { put(g, 14, y, C.frame); put(g, 15, y, C.frameDark); put(g, 16, y, C.frame); }
  for (let x = 9; x <= 21; x++) { put(g, x, B + 5, C.frame); put(g, x, B + 6, C.frameDark); }
  // 電源ランプ
  put(g, 25, B - 1, C.accent);
}

export const MONITOR_BODY: BodySpec = {
  draw: (g: Grid) => drawMonitor(g),
  faceX: 10,
  faceY: 6,
  armY: 13,
  armLeftX: 0,
  armRightX: 31,
  // 台座があるので足は要らない
  legY: 30,
  legLeftX: 12,
  legRightX: 17,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 3,
};

// ============================================================
// アーケード筐体くん — 縦長。上の看板と、画面＝顔
// ============================================================

function drawArcade(g: Grid) {
  const L = 6, R = 25, T = 1, B = 28;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) put(g, x, y, C.frameDark);
  }
  // 看板（光る帯）
  for (let x = L + 1; x <= R - 1; x++) { put(g, x, T + 1, C.accent); put(g, x, T + 2, C.accent); }
  // 画面＝顔
  for (let y = 6; y <= 19; y++) {
    for (let x = L + 2; x <= R - 2; x++) {
      const lit = (x - 15.5) + (y - 12);
      put(g, x, y, lit < -8 ? C.hubLight : lit > 8 ? C.hubDark : C.hub);
    }
  }
  // 操作パネル（ボタン2つとレバー）
  for (let x = L + 1; x <= R - 1; x++) put(g, x, 21, C.frame);
  put(g, 12, 23, C.danger);
  put(g, 15, 23, C.warn);
  put(g, 19, 23, C.frameLight);
  put(g, 19, 22, C.frameLight);
}

export const ARCADE_BODY: BodySpec = {
  draw: (g: Grid) => drawArcade(g),
  faceX: 10,
  faceY: 7,
  armY: 14,
  armLeftX: 2,
  armRightX: 29,
  legY: 29,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 12,
  hangRightX: 18,
  topY: 1,
};

// ============================================================
// 携帯機くん — 横長の小型機。中央の画面が顔
// ============================================================

function drawHandheld(g: Grid) {
  const L = 2, R = 29, T = 8, B = 24;
  for (let y = T; y <= B; y++) {
    for (let x = L; x <= R; x++) {
      const cx = x < L + 2 ? L + 2 - x : x > R - 2 ? x - (R - 2) : 0;
      const cy = y < T + 1 ? T + 1 - y : y > B - 1 ? y - (B - 1) : 0;
      if (cx + cy > 2) continue;
      const lit = (x - 15.5) + (y - 16);
      put(g, x, y, lit < -10 ? C.frame : lit > 10 ? C.bladeGap : C.frameDark);
    }
  }
  // 画面＝顔
  for (let y = 10; y <= 22; y++) {
    for (let x = 9; x <= 22; x++) {
      const edge = x === 9 || x === 22 || y === 10 || y === 22;
      put(g, x, y, edge ? C.outline : (x - 15.5) + (y - 16) < -6 ? C.hubLight : C.hub);
    }
  }
  // 十字キーと2つのボタン
  put(g, 6, 16, C.frameLight); put(g, 5, 16, C.frameLight); put(g, 7, 16, C.frameLight);
  put(g, 6, 15, C.frameLight); put(g, 6, 17, C.frameLight);
  put(g, 25, 15, C.danger); put(g, 27, 17, C.accent);
}

export const HANDHELD_BODY: BodySpec = {
  draw: (g: Grid) => drawHandheld(g),
  faceX: 10,
  faceY: 11,
  armY: 17,
  armLeftX: 0,
  armRightX: 31,
  legY: 25,
  legLeftX: 11,
  legRightX: 18,
  hangLeftX: 11,
  hangRightX: 18,
  topY: 8,
};


/**
 * キャラid → 体。characters.ts と id を揃えること。
 * ここに無い id を引いたら CPUクーラーで代用する（絵が出ないより良い）。
 */
export const BODIES: Record<string, BodySpec> = {
  "cpu-cooler": COOLER_BODY,
  "cpu": CPU_BODY,
  gpu: GPU_BODY,
  memory: MEMORY_BODY,
  motherboard: MOTHERBOARD_BODY,
  psu: PSU_BODY,
  hdd: HDD_BODY,
  ssd: SSD_BODY,
  m2: M2_BODY,
  mouse: MOUSE_BODY,
  keyboard: KEYBOARD_BODY,
  monitor: MONITOR_BODY,
  arcade: ARCADE_BODY,
  handheld: HANDHELD_BODY,
};

export function bodyOf(id: string): BodySpec {
  return BODIES[id] ?? COOLER_BODY;
}
