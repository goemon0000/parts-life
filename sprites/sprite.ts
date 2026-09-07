import { C } from "./palette";

/**
 * CPUクーラーくんのドット絵を「手続き的に」描く。
 *
 * ■なぜ数値配列を直接持たないか
 * 32x32 を 8モーション x 数フレーム分そのまま持つと 25,000 個近い数字になり、
 * 人間もAIも中身を検算できない（＝作れない・直せない）。
 * この形なら **基準は1つの描画コードだけ**で、フレーム間の形の揺らぎが原理的に起きない。
 * 羽根の回転も「角度を足す」だけで正確に出せる。
 *
 * ■出力
 * 1フレーム = Uint8Array(32*32)。値はパレット添字（src/lib/mascot/palette.ts）。
 * 実行時にこれを ImageData へ展開して canvas に置く。
 */

export const SPRITE_SIZE = 32;

export type Grid = Uint8Array;

/**
 * キャラごとに違うのは「体の絵」と「顔・手足の付き位置」だけ。
 * 動き（歩く・座る・ぶら下がる…）とポーズの合成は全キャラ共通なので、
 * キャラを足すときはこの定義を1つ書けばよい。
 */
export type BodySpec = {
  /** 体を描く。spin は 0..1 の位相（ファン等、回るものを持つキャラ用） */
  draw: (g: Grid, spin: number) => void;
  /** 顔グリッド(12x12)を重ねる左上 */
  faceX: number;
  faceY: number;
  /** 腕: 付け根の y と、体の左右の外端 x */
  armY: number;
  armLeftX: number;
  armRightX: number;
  /** 足: 付け根の y と、左右の足の x */
  legY: number;
  legLeftX: number;
  legRightX: number;
  /** ぶら下がるときに腕を出す x（左右） */
  hangLeftX: number;
  hangRightX: number;
  /** 体の上端 y。ぶら下がる腕をここまで伸ばして体に繋げる */
  topY: number;
};

const idx = (x: number, y: number) => y * SPRITE_SIZE + x;

export function put(g: Grid, x: number, y: number, c: number) {
  if (x < 0 || y < 0 || x >= SPRITE_SIZE || y >= SPRITE_SIZE) return;
  if (c === C.none) return;
  g[idx(x, y)] = c;
}

/** 角を丸めた正方形の内側か。枠の外形に使う。 */
export function insideRoundedSquare(dx: number, dy: number, half: number, corner: number) {
  const ax = Math.abs(dx);
  const ay = Math.abs(dy);
  if (ax > half || ay > half) return false;
  const ox = ax - (half - corner);
  const oy = ay - (half - corner);
  if (ox <= 0 || oy <= 0) return true;
  return ox * ox + oy * oy <= corner * corner;
}


/**
 * 腕。左右それぞれ上下にずらせる。
 * 体が 24px もあるので腕は短い。**細くすると背景に溶けて消える**ため、
 * 3x2 の塊にして枠と同じ明るい色で描く。
 */
function drawArm(g: Grid, body: BodySpec, side: -1 | 1, dy: number) {
  const y0 = body.armY + dy;
  const xs = side < 0
    ? [body.armLeftX + 2, body.armLeftX + 1, body.armLeftX]
    : [body.armRightX - 2, body.armRightX - 1, body.armRightX];
  put(g, xs[0], y0, C.frame);
  put(g, xs[1], y0, C.frameLight);
  put(g, xs[2], y0, C.frameLight);
  put(g, xs[0], y0 + 1, C.frameDark);
  put(g, xs[1], y0 + 1, C.frame);
  put(g, xs[2], y0 + 1, C.frame);
}

/**
 * 足。dx で左右、lift で「足を上げる量」。
 *
 * すねの根元は常に枠の底(y=26)に固定し、**縮めることで足を上げる**。
 * 足全体を上下にずらす作りにすると、体の上下(bodyDy)と二重にかかって
 * 胴から外れた点が宙に浮く（実機の確認で判明）。
 */
/** 足の長さ（付け根から足先まで） */
const LEG_LENGTH = 2;

function drawLeg(g: Grid, body: BodySpec, side: -1 | 1, dx: number, lift: number) {
  const x0 = (side < 0 ? body.legLeftX : body.legRightX) + dx;
  const top = body.legY;
  const footY = top + LEG_LENGTH - lift;
  for (let y = top; y < footY; y++) {
    put(g, x0, y, C.frame);
    put(g, x0 + 1, y, C.frameDark);
  }
  // 足先（外側へ広がる）
  put(g, side < 0 ? x0 - 1 : x0 + 2, footY, C.frameLight);
  put(g, x0, footY, C.frame);
  put(g, x0 + 1, footY, C.frame);
}

/**
 * ぶら下がる腕。**体を下げた後に、絵の上端へ固定して描く。**
 * 横に出す腕(drawArm)を上げるだけでは、腕が枠の高さの範囲から出られず
 * 「頭の上でつかんでいる」形にならない。
 */
/**
 * ぶら下がる腕。手は絵の上端に固定し、**体の上端まで伸ばして繋げる**。
 * 伸ばす先を決め打ちにすると、体の高さが違うキャラで腕が宙に浮く
 * （CPUくんを足したときに実際に浮いた）。
 */
function drawHangArms(g: Grid, body: BodySpec, sway: number, bodyTop: number) {
  for (const side of [-1, 1] as const) {
    // 2px幅にする。1pxだとアンテナのように見えて腕に読めない。
    const x = (side < 0 ? body.hangLeftX : body.hangRightX) + sway;
    for (let y = 2; y <= Math.max(4, bodyTop); y++) {
      put(g, x, y, C.frame);
      put(g, x + 1, y, C.frameDark);
    }
    // 手（線をつかんでいる形）
    for (let hx = x - 1; hx <= x + 2; hx++) put(g, hx, 1, C.frameLight);
  }
}

/**
 * 輪郭を1px足す。
 * 明るいハブと暗い背景だけでも形は読めるが、カード背景(#0e1118)や
 * 画像の上に乗ったときに縁が溶けるのを防ぐ。
 */
function addOutline(g: Grid): Grid {
  const out = new Uint8Array(g);
  for (let y = 0; y < SPRITE_SIZE; y++) {
    for (let x = 0; x < SPRITE_SIZE; x++) {
      if (g[idx(x, y)] !== C.none) continue;
      let touches = false;
      for (const [ox, oy] of [[1, 0], [-1, 0], [0, 1], [0, -1]] as const) {
        const nx = x + ox;
        const ny = y + oy;
        if (nx < 0 || ny < 0 || nx >= SPRITE_SIZE || ny >= SPRITE_SIZE) continue;
        if (g[idx(nx, ny)] !== C.none) { touches = true; break; }
      }
      if (touches) out[idx(x, y)] = C.outline;
    }
  }
  return out;
}

/** 縦にずらす（歩行のはずみ等）。中身を作り直さずフレームを流用するため。 */
function shiftY(g: Grid, dy: number): Grid {
  if (dy === 0) return g;
  const out = new Uint8Array(SPRITE_SIZE * SPRITE_SIZE);
  for (let y = 0; y < SPRITE_SIZE; y++) {
    const sy = y - dy;
    if (sy < 0 || sy >= SPRITE_SIZE) continue;
    out.set(g.subarray(sy * SPRITE_SIZE, sy * SPRITE_SIZE + SPRITE_SIZE), y * SPRITE_SIZE);
  }
  return out;
}

// ---------------------------------------------------------------------------
// 表情
// ---------------------------------------------------------------------------

/**
 * 表情は 12x12 の文字グリッドで持つ。ハブの左上 (10, 8) に重ねる。
 * `.` は「下地をそのまま残す」。文字で書くのは、**目で形を検算できるようにするため**。
 *   k = インク / w = ハイライト / c = ほお
 *
 * 注意: sit や climb で体が傾くフレームを足すときは、この重ね位置を
 * フレームごとに持たせること（表情の位置をキャラ単位で固定しない）。
 */
const FACE_CHARS: Record<string, number> = {
  k: C.ink,
  w: C.hubLight,
  c: C.cheek,
};

export type FaceId = "normal" | "wide" | "smile" | "sleepy" | "worried";

const FACES: Record<FaceId, string[]> = {
  // 通常（縦長の点目）
  normal: [
    "............",
    "............",
    "............",
    "...kk..kk...",
    "...kk..kk...",
    "...kk..kk...",
    "............",
    ".....kk.....",
    "............",
    "............",
    "............",
    "............",
  ],
  // 驚き（見開き＋口が開く）
  wide: [
    "............",
    "............",
    "..kkk..kkk..",
    "..kwk..kwk..",
    "..kkk..kkk..",
    "..kkk..kkk..",
    "............",
    "....kkkk....",
    "....k..k....",
    "....kkkk....",
    "............",
    "............",
  ],
  // 喜び（弧の目・への字の逆）
  smile: [
    "............",
    "............",
    "............",
    "...k....k...",
    "..k.k..k.k..",
    "..c.....c...",
    "............",
    "....k..k....",
    ".....kk.....",
    "............",
    "............",
    "............",
  ],
  // 眠い（半目）
  sleepy: [
    "............",
    "............",
    "............",
    "............",
    "..kkkk.kkkk.",
    "...k....k...",
    "............",
    ".....kk.....",
    "............",
    "............",
    "............",
    "............",
  ],
  // 困り（八の字眉）
  worried: [
    "............",
    "............",
    "..k......k..",
    "...kk..kk...",
    "............",
    "...kk..kk...",
    "...kk..kk...",
    "............",
    "....kkkk....",
    "............",
    "............",
    "............",
  ],
};

function drawFace(g: Grid, body: BodySpec, face: FaceId, dx: number, dy: number) {
  const rows = FACES[face];
  for (let r = 0; r < rows.length; r++) {
    const row = rows[r];
    for (let c = 0; c < row.length; c++) {
      const color = FACE_CHARS[row[c]];
      if (color === undefined) continue;
      put(g, body.faceX + c + dx, body.faceY + r + dy, color);
    }
  }
}

/**
 * 上に行くほど横にずらす＝前傾／後傾。
 * 正面向きのキャラなので「覗き込む」「よじ登る」を体の向きで描けない。
 * 傾きだけでも意味は伝わるので、専用のフレームを持たずここで済ませる。
 */
function shearX(g: Grid, amount: number): Grid {
  if (amount === 0) return g;
  const out = new Uint8Array(SPRITE_SIZE * SPRITE_SIZE);
  for (let y = 0; y < SPRITE_SIZE; y++) {
    // 傾きの中心は絵の中央。キャラごとの体の中心に合わせる必要はない
    const dx = Math.round((((SPRITE_SIZE - 1) / 2 - y) / SPRITE_SIZE) * amount * 2);
    for (let x = 0; x < SPRITE_SIZE; x++) {
      const v = g[idx(x, y)];
      if (v === C.none) continue;
      const nx = x + dx;
      if (nx < 0 || nx >= SPRITE_SIZE) continue;
      out[idx(nx, y)] = v;
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// フレーム合成
// ---------------------------------------------------------------------------

export type Pose = {
  /** 羽根の回転位相 0..1（1で羽根1枚分） */
  fan: number;
  face: FaceId;
  /** 体全体の上下（歩行のはずみ等） */
  bodyDy?: number;
  /** 腕の上下 [左, 右] */
  arms?: [number, number];
  /** 足の [左右, 足上げ量] [左, 右] */
  legs?: [[number, number], [number, number]];
  /** 足を描かない（座り・ぶら下がり用） */
  hideLegs?: boolean;
  /** 横に出す腕を描かない（ぶら下がり用） */
  hideArms?: boolean;
  /** 頭の上へ両腕を伸ばしてぶら下がる。数値は左右の揺れ */
  hangArms?: number;
  /** 顔の位置（視線をずらす。ハブの中で数px動かせる） */
  faceDx?: number;
  faceDy?: number;
  /** 前傾・後傾。正の値で上部が右へ */
  shear?: number;
};

export function renderPose(pose: Pose, body: BodySpec): Grid {
  const g = new Uint8Array(SPRITE_SIZE * SPRITE_SIZE);
  const arms = pose.arms ?? [0, 0];
  const legs = pose.legs ?? [[0, 0], [0, 0]];

  // 奥から手前へ: 足 → 腕 → 枠 → 羽根 → ハブ → 顔
  if (!pose.hideLegs) {
    drawLeg(g, body, -1, legs[0][0], legs[0][1]);
    drawLeg(g, body, 1, legs[1][0], legs[1][1]);
  }
  if (!pose.hideArms) {
    drawArm(g, body, -1, arms[0]);
    drawArm(g, body, 1, arms[1]);
  }
  body.draw(g, pose.fan);
  drawFace(g, body, pose.face, pose.faceDx ?? 0, pose.faceDy ?? 0);

  const posed = shiftY(shearX(g, pose.shear ?? 0), pose.bodyDy ?? 0);
  // ぶら下がる腕だけは体の移動に付いていかない（つかんでいる位置は動かない）
  if (pose.hangArms !== undefined) {
    drawHangArms(posed, body, pose.hangArms, body.topY + (pose.bodyDy ?? 0));
  }
  return addOutline(posed);
}
