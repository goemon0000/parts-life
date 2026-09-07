import type { FaceId, Pose } from "./sprite";
import type { MarkId } from "./marks";

/**
 * モーション定義。
 *
 * フェーズ1は 4種だけ（idle / walk / sit / surprised）。
 * peek / happy / sleep / climb は仕組みが固まってからポーズを足すだけで増える。
 * ここに「何フレームを何msで送るか」も持たせ、描画側は再生するだけにする。
 */
export type Motion = {
  frames: Pose[];
  /** 1フレームの表示時間(ms) */
  frameMs: number;
  /** 一巡したら次のモーションへ抜ける（surprised 等） */
  once?: boolean;
  /**
   * 足場の線に合わせる点。絵の上端からの割合。
   *   0.94 … 足の裏（立つ・歩く・登る）
   *   0.80 … 尻（座って足を線の下へ垂らす）
   *   0.06 … 手（線にぶら下がって体は下）
   * これを持たないと、ぶら下がりが「線の上に浮いている」ように見える。
   */
  anchor?: number;
  /**
   * ロープを何処に留めるか（絵の上端からの割合）。持たないモーションはロープ無し。
   * ぶら下がりでロープを出さないと、掴む対象が画面に無いので
   * 「腕を上げているだけ」に見える（実機の確認で判明）。
   */
  rope?: number;
  /**
   * 頭の横に出す記号。**言葉ではなく記号**なので、喋らせない決めとは両立する。
   * フォントで描くと環境で形が変わるので、ドット絵で持つ（marks.ts）。
   */
  mark?: MarkId;
};

/** 既定の接地点＝足の裏。 */
export const DEFAULT_ANCHOR = 0.94;

/** 羽根は常に回る。4フレームでちょうど1周期になる。 */
const fanPhase = (i: number) => (i % 4) / 4;

function spin(count: number, face: FaceId, extra: (i: number) => Partial<Pose> = () => ({})): Pose[] {
  return Array.from({ length: count }, (_, i) => ({
    fan: fanPhase(i),
    face,
    ...extra(i),
  }));
}

export const MOTIONS: Record<string, Motion> = {
  // 待機。羽根が回っているので止まっていても生きて見える。
  idle: {
    frames: spin(4, "normal", (i) => ({ bodyDy: i === 2 ? 1 : 0 })),
    frameMs: 140,
  },

  // 横移動。足を交互に出し、体が1pxはずむ。
  walk: {
    frames: spin(4, "normal", (i) => ({
      bodyDy: i % 2 === 0 ? 0 : -1,
      arms: (i % 2 === 0 ? [1, -1] : [-1, 1]) as [number, number],
      // 正面向きなので足の「前後」は描き分けられない。**片足ずつ上げる**ことで歩きを見せる。
      legs: (i % 2 === 0
        ? [[0, 1], [0, 0]]
        : [[0, 0], [0, 1]]) as [[number, number], [number, number]],
    })),
    frameMs: 120,
  },

  // 座る（見出しの上でぶら下がる）。体を下げ、足を近づけて垂らす。
  // 32pxでは「座り」の形そのものは描き分けられないので、
  // **位置が下がっている・足が揺れている・羽根が遅い**の3点で読ませる。
  sit: {
    frames: spin(4, "normal", (i) => ({
      bodyDy: 2,
      arms: [2, 2] as [number, number],
      // 足はたたんで短く。左右を内側に寄せて「ちょこんと座っている」形にする。
      legs: (i < 2
        ? [[1, 0], [-1, 0]]
        : [[1, 0], [-1, 1]]) as [[number, number], [number, number]],
    })),
    frameMs: 300,
    // 尻を線に合わせ、足は線の下へ垂らす。縁に腰かけて見せるため。
    anchor: 0.80,
  },

  // ロープを伝って上下する。画面外への出入りに使う。
  // 正面向きなので「登っている」形は描けない。**腕を交互に上げ、体を左右に振る**
  // ことで、上下移動と合わせて登り降りに見せる。
  climb: {
    frames: spin(4, "normal", (i) => ({
      arms: (i % 2 === 0 ? [-5, 0] : [0, -5]) as [number, number],
      legs: [[0, 1], [0, 1]] as [[number, number], [number, number]],
      shear: i % 2 === 0 ? 1 : -1,
      bodyDy: i < 2 ? 0 : 1,
    })),
    frameMs: 130,
    rope: 0.34,
  },

  // 覗き込む。前傾して視線を下げる（スペック表を見ている想定）。
  peek: {
    frames: spin(4, "wide", (i) => ({
      // 傾きを 2 にすると行ごとのずれが大きく、ハブの円と目が歪んで見えた。
      // 1 に抑え、視線（faceDy）側で「下を見ている」を補う。
      shear: 1,
      faceDy: 2,
      faceDx: 1,
      bodyDy: i === 1 || i === 2 ? 1 : 0,
      arms: [1, -1] as [number, number],
      legs: [[-1, 0], [1, 0]] as [[number, number], [number, number]],
    })),
    frameMs: 220,
  },

  // 喜ぶ。跳ねる。
  happy: {
    frames: spin(4, "smile", (i) => ({
      bodyDy: [0, -3, -4, -1][i],
      arms: ([[-1, -1], [-4, -4], [-5, -5], [-2, -2]] as [number, number][])[i],
      legs: ([[[0, 0], [0, 0]], [[0, 2], [0, 2]], [[0, 2], [0, 2]], [[0, 1], [0, 1]]] as [[number, number], [number, number]][])[i],
    })),
    frameMs: 130,
    mark: "note",
  },

  // 寝る。羽根も落ちる（回転が遅い）。体がゆっくり上下する。
  sleep: {
    frames: [
      { fan: 0, face: "sleepy", bodyDy: 2, arms: [2, 2], legs: [[1, 2], [-1, 2]] },
      { fan: 0.25, face: "sleepy", bodyDy: 3, arms: [3, 3], legs: [[1, 2], [-1, 2]] },
      { fan: 0.5, face: "sleepy", bodyDy: 3, arms: [3, 3], legs: [[1, 2], [-1, 2]] },
      { fan: 0.75, face: "sleepy", bodyDy: 2, arms: [2, 2], legs: [[1, 2], [-1, 2]] },
    ],
    frameMs: 620,
    mark: "zzz",
  },

  // ぶら下がる。見出しや表の縁を手でつかんで体を垂らす。
  // 接地点が手なので anchor が他と全く違う（0.06）。
  hang: {
    frames: spin(4, "normal", (i) => ({
      bodyDy: 3 + (i === 1 || i === 2 ? 1 : 0),
      hideArms: true,
      hangArms: [0, 1, 0, -1][i],
      legs: [[0, 1], [0, 1]] as [[number, number], [number, number]],
    })),
    frameMs: 240,
    anchor: 0.06,
    rope: 0.05,
  },

  // 驚く。目を見開き、腕が上がる。1巡したら idle に戻る。
  surprised: {
    frames: [
      { fan: 0, face: "wide", bodyDy: -2, arms: [-3, -3], legs: [[0, 1], [0, 1]] },
      { fan: 0.5, face: "wide", bodyDy: -3, arms: [-4, -4], legs: [[0, 2], [0, 2]] },
      { fan: 0.25, face: "wide", bodyDy: -1, arms: [-2, -2], legs: [[0, 1], [0, 1]] },
    ],
    frameMs: 110,
    once: true,
    mark: "excl",
  },
};
