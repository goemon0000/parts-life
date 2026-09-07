import type { LedgeKind } from "./targets";

/**
 * 性格はパラメータで表す。**専用モーションを増やさない。**
 * キャラを足すたびに8モーション作り直しになるのを避けるため、
 * 共通モーションの速度・頻度・行き先の偏りだけで描き分ける。
 */
export type Personality = {
  /** 歩く速さ (px/秒) */
  walkSpeed: number;
  /** 登り降りの速さ (px/秒) */
  climbSpeed: number;
  /** 立ち止まっている時間の範囲 (ms) */
  idleMs: [number, number];
  /** 足場に着いてから留まる時間の範囲 (ms) */
  restMs: [number, number];
  /** 無操作が何秒続いたら寝るか */
  sleepAfterMs: number;
  /** 1ページあたりの滞在時間の範囲 (ms)。過ぎたら退場する */
  stayMs: [number, number];
  /** 退場から次の登場までの「不在」の範囲 (ms) */
  absentMs: [number, number];
  /** 行き先の偏り。1が基準 */
  bias: Partial<Record<LedgeKind, number>>;
};

/**
 * CPUクーラーくん — 心配性・過保護。
 * **冷やす相手はCPU一つだけ**で、機械の頭を熱から守る係。
 * だから「目を離さない・よく動く・あまり寝ない」。
 * （「ほかの部品を冷やす」は誤り。役割から性格を導く以上、ここを間違えると設定全体が崩れる）
 * スペック表の偏りを 2.0 にしているのは、覗き込む動作が
 * 視線誘導として一番効くと判断したため（商品リンクへ寄せない代わり）。
 */
export const CPU_COOLER: Personality = {
  walkSpeed: 46,
  climbSpeed: 105,
  idleMs: [2600, 5200],
  restMs: [6500, 13000],
  sleepAfterMs: 180_000,
  stayMs: [34_000, 62_000],
  absentMs: [6_000, 15_000],
  bias: { spec: 2.0, perch: 1.0, related: 1.0, edge: 0.5 },
};

export function randRange([lo, hi]: [number, number]): number {
  return lo + Math.random() * (hi - lo);
}

/**
 * CPUくん — 生真面目・働き者。
 * 計算をぜんぶ引き受けているので「動きが速い・休憩が短い」。
 * 逆に、様子を見に行く役ではないので覗きに行く頻度はクーラーより低い。
 */
export const CPU_UNIT: Personality = {
  walkSpeed: 62,
  climbSpeed: 120,
  idleMs: [1400, 3000],
  restMs: [4000, 8000],
  sleepAfterMs: 240_000,
  stayMs: [30_000, 55_000],
  absentMs: [6_000, 14_000],
  bias: { spec: 1.4, perch: 1.0, related: 1.0, edge: 0.6 },
};

/**
 * GPUくん — 派手・自信家。
 * いちばん大きく、いちばん電気を食い、いちばん高い。光りもする。
 * 「動きが大きい・反応が大げさ」をここで出す。
 */
export const GPU_UNIT: Personality = {
  walkSpeed: 50,
  climbSpeed: 115,
  idleMs: [1800, 3600],
  restMs: [5000, 10_000],
  sleepAfterMs: 150_000,
  stayMs: [32_000, 58_000],
  absentMs: [6_000, 14_000],
  bias: { spec: 2.2, perch: 1.2, related: 1.0, edge: 0.4 },
};

/**
 * メモリくん — 忘れっぽい・のんき。
 * 電気が切れると中身がぜんぶ消える部品なので、
 * 「移動の途中で止まる」＝立ち止まりが多く、歩みも遅い。
 */
export const MEMORY_UNIT: Personality = {
  walkSpeed: 34,
  climbSpeed: 85,
  idleMs: [4000, 8000],
  restMs: [7000, 14_000],
  sleepAfterMs: 90_000,
  stayMs: [30_000, 52_000],
  absentMs: [7_000, 16_000],
  bias: { spec: 0.9, perch: 1.4, related: 1.0, edge: 1.0 },
};

/**
 * マザーボードくん — 司令塔・まとめ役。
 *
 * 当初の設定表では「縁の下・繋ぐだけ」としていたが、実際には
 * **各部品への給電、クロックの配分、起動の順番まで仕切っている**。
 * 「繋ぐだけの人」ではないので、性格も動かない裏方ではなく
 * 「動かずに全体を見ている」に寄せる。
 */
export const MOTHERBOARD_UNIT: Personality = {
  walkSpeed: 30,
  climbSpeed: 80,
  idleMs: [5000, 10_000],
  restMs: [9000, 16_000],
  sleepAfterMs: 200_000,
  stayMs: [36_000, 62_000],
  absentMs: [8_000, 16_000],
  bias: { spec: 1.6, perch: 1.2, related: 1.2, edge: 0.6 },
};

/**
 * 電源くん — 無口。ただし全部品の生殺与奪を握る。
 *
 * 「地味で目立たない」ではなく、**ここが落ちると全部が同時に落ちる**。
 * 反応は薄いが、離れないし寝ない、という形にする。
 */
export const PSU_UNIT: Personality = {
  walkSpeed: 32,
  climbSpeed: 78,
  idleMs: [5000, 11_000],
  restMs: [9000, 16_000],
  sleepAfterMs: 300_000,
  stayMs: [40_000, 70_000],
  absentMs: [8_000, 18_000],
  bias: { spec: 1.2, perch: 1.0, related: 0.8, edge: 1.0 },
};

/** HDDくん — 老人・寡黙で頼れる。古参で大容量、そのかわり遅い。 */
export const HDD_UNIT: Personality = {
  walkSpeed: 22, climbSpeed: 60,
  idleMs: [5000, 11_000], restMs: [10_000, 18_000],
  sleepAfterMs: 45_000, stayMs: [34_000, 60_000], absentMs: [8_000, 18_000],
  bias: { spec: 1.0, perch: 1.8, related: 0.8, edge: 1.2 },
};

/** SSDくん — せっかち・若者。速いが容量は控えめ。落ち着きがない。 */
export const SSD_UNIT: Personality = {
  walkSpeed: 78, climbSpeed: 140,
  idleMs: [900, 2200], restMs: [3000, 6000],
  sleepAfterMs: 200_000, stayMs: [26_000, 46_000], absentMs: [5_000, 11_000],
  bias: { spec: 1.6, perch: 0.9, related: 1.2, edge: 0.7 },
};

/** マウスくん — 落ち着きない・小動物。とにかく素早い。 */
export const MOUSE_UNIT: Personality = {
  walkSpeed: 88, climbSpeed: 150,
  idleMs: [700, 1800], restMs: [2500, 5000],
  sleepAfterMs: 120_000, stayMs: [24_000, 42_000], absentMs: [5_000, 10_000],
  bias: { spec: 0.8, perch: 1.6, related: 1.2, edge: 1.4 },
};

/** キーボードくん — おしゃべり。動きが細かい。 */
export const KEYBOARD_UNIT: Personality = {
  walkSpeed: 44, climbSpeed: 95,
  idleMs: [1200, 2600], restMs: [4500, 9000],
  sleepAfterMs: 150_000, stayMs: [30_000, 52_000], absentMs: [6_000, 13_000],
  bias: { spec: 1.0, perch: 1.4, related: 1.6, edge: 0.9 },
};

/** モニターくん — 表情豊か・素直。画面がそのまま顔なので、感情がよく出る。 */
export const MONITOR_UNIT: Personality = {
  walkSpeed: 38, climbSpeed: 90,
  idleMs: [2000, 4500], restMs: [6000, 12_000],
  sleepAfterMs: 100_000, stayMs: [32_000, 56_000], absentMs: [7_000, 15_000],
  bias: { spec: 1.4, perch: 1.2, related: 1.2, edge: 0.8 },
};

/** アーケード筐体くん — 呼び込み役。その場から動かず、人を待つ。 */
export const ARCADE_UNIT: Personality = {
  walkSpeed: 26, climbSpeed: 70,
  idleMs: [4000, 9000], restMs: [9000, 16_000],
  sleepAfterMs: 130_000, stayMs: [34_000, 60_000], absentMs: [8_000, 16_000],
  bias: { spec: 0.7, perch: 1.6, related: 1.4, edge: 1.0 },
};

/** 携帯機くん — どこへでも付いてくる。小さいので身軽。 */
export const HANDHELD_UNIT: Personality = {
  walkSpeed: 66, climbSpeed: 130,
  idleMs: [1200, 2800], restMs: [3500, 7000],
  sleepAfterMs: 110_000, stayMs: [26_000, 48_000], absentMs: [5_000, 12_000],
  bias: { spec: 0.8, perch: 1.3, related: 1.3, edge: 1.2 },
};
