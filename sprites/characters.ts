import {
  ARCADE_UNIT, CPU_COOLER, CPU_UNIT, GPU_UNIT, HANDHELD_UNIT, HDD_UNIT,
  KEYBOARD_UNIT, MEMORY_UNIT, MONITOR_UNIT, MOTHERBOARD_UNIT, MOUSE_UNIT,
  PSU_UNIT, SSD_UNIT,
  type Personality,
} from "./personality";

/**
 * 登場するキャラクターの一覧。
 *
 * フェーズ1は CPUクーラーくん 1体だけだが、**表示・非表示を個別に切り替える**
 * 都合で最初から一覧として持つ。キャラを足すときはここに1件足し、
 * 絵（sprite）と性格（personality）を差せばよい。
 */
export type CharacterId =
  | "cpu-cooler"
  | "cpu"
  | "gpu"
  | "memory"
  | "motherboard"
  | "psu"
  | "hdd"
  | "ssd"
  | "mouse"
  | "keyboard"
  | "monitor"
  | "arcade"
  | "handheld";

/** どの種類のページに出るか。 */
export type Family = "pc" | "game";

export type Character = {
  id: CharacterId;
  family: Family;
  name: { ja: string; en: string };
  motif: { ja: string; en: string };
  trait: { ja: string; en: string };
  /**
   * 性格の根拠。部品の**実際の役割**から導く。
   * ここを外すと設定全体が嘘になる（CPUクーラーを「ほかの部品を冷やす係」と
   * 書いていたが、冷やす相手はCPU一つだけ。2026-09-07 に指摘されて修正）。
   */
  why: { ja: string; en: string };
  personality: Personality;
  /**
   * 免責。**この子たちが見せる規格や相性は遊びであって、購入の判断材料ではない。**
   * 実在の規格を元に判定してはいるが、型番や版数まで見ているわけではないので、
   * 本当の可否は必ず製品の仕様で確かめてもらう必要がある。
   * キャラごとに持たせているのは、キャラを足したときに書き忘れないようにするため。
   */
  disclaimer: { ja: string; en: string };
};

/** 全キャラ共通の免責文。個別に変える必要が出たらキャラ側で上書きする。 */
const COMMON_DISCLAIMER = {
  ja: "この子の話すことは遊びです。規格や相性の表示も雰囲気づくりのためのもので、購入の判断には使えません。実際の対応可否は必ず製品の仕様をご確認ください。",
  en: "Anything this character says is for fun. The specs and compatibility marks are flavour, not buying advice — always check the actual product specifications.",
} as const;

export const CHARACTERS: Character[] = [
  {
    id: "cpu-cooler",
    family: "pc",
    name: { ja: "CPUクーラーくん", en: "Mr. CPU Cooler" },
    motif: { ja: "CPUクーラー（ファンが顔）", en: "A CPU cooler — the fan is its face" },
    trait: { ja: "心配性・過保護", en: "A worrier, overprotective" },
    why: {
      ja: "守る相手はCPUただ一つ。機械の頭が熱でまいらないよう見張る係で、目を離すとすぐ熱が上がる。だから落ち着きなく見て回るし、あまり寝ない。",
      en: "It looks after exactly one part: the CPU. Keeping the machine's head from overheating is its whole job, so it never really stops checking — always moving, rarely sleeping.",
    },
    personality: CPU_COOLER,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "cpu",
    family: "pc",
    name: { ja: "CPUくん", en: "Mr. CPU" },
    motif: { ja: "CPU（ヒートスプレッダが顔）", en: "A CPU — the heat spreader is its face" },
    trait: { ja: "生真面目・働き者", en: "Earnest and hard-working" },
    why: {
      ja: "計算はぜんぶこの子が引き受けている。休むと機械が止まるので、動きが速く、休憩が短い。",
      en: "Every calculation goes through it. The machine stops when it rests, so it moves fast and takes short breaks.",
    },
    personality: CPU_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "gpu",
    family: "pc",
    name: { ja: "GPUくん", en: "Mr. GPU" },
    motif: { ja: "グラフィックスカード（ファンが顔）", en: "A graphics card — the fan is its face" },
    trait: { ja: "派手・自信家", en: "Flashy and self-assured" },
    why: {
      ja: "いちばん大きく、いちばん電気を食い、いちばん高い。おまけに光る。だから動きも反応も大きい。",
      en: "The biggest, the hungriest, the most expensive part — and it lights up. So it moves big and reacts big.",
    },
    personality: GPU_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "memory",
    family: "pc",
    name: { ja: "メモリくん", en: "Ms. Memory" },
    motif: { ja: "メモリモジュール（ヒートスプレッダが顔）", en: "A memory module — the heat spreader is its face" },
    trait: { ja: "忘れっぽい・のんき", en: "Forgetful and easygoing" },
    why: {
      ja: "電気が切れると中身がぜんぶ消えてしまう。だから急がないし、歩いている途中でよく立ち止まる。",
      en: "Everything it holds vanishes the moment the power goes. So it never hurries, and it often stops mid-walk.",
    },
    personality: MEMORY_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "motherboard",
    family: "pc",
    name: { ja: "マザーボードくん", en: "Mr. Motherboard" },
    motif: { ja: "マザーボード（ソケットが顔）", en: "A motherboard — the socket is its face" },
    trait: { ja: "司令塔・まとめ役", en: "The one who runs the room" },
    why: {
      ja: "ただ繋いでいるだけではなく、各部品への給電もクロックの配分も起動の順番も、この子が決めている。だからあまり動かず、全体を見ている。",
      en: "It does more than connect things: power delivery, clock distribution and the boot order are all its call. So it stays put and watches everything.",
    },
    personality: MOTHERBOARD_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "psu",
    family: "pc",
    name: { ja: "電源くん", en: "Mr. PSU" },
    motif: { ja: "電源ユニット（銘板が顔）", en: "A power supply — the label is its face" },
    trait: { ja: "無口・職人気質", en: "Quiet, and all craft" },
    why: {
      ja: "地味に見えるが、ここが落ちると全部が同時に落ちる。だから反応は薄いのに、持ち場を離れないし寝ない。",
      en: "It looks plain, but everything dies the instant it does. It barely reacts — and it never leaves its post or sleeps.",
    },
    personality: PSU_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "hdd",
    family: "pc",
    name: { ja: "HDDくん", en: "Mr. HDD" },
    motif: { ja: "ハードディスク（円盤が顔）", en: "A hard disk — the platter is its face" },
    trait: { ja: "老人・寡黙で頼れる", en: "Old, quiet, dependable" },
    why: {
      ja: "いちばん古くからいて、いちばん多く覚えている。そのかわり動きは遅く、放っておくとすぐ止まって休む。",
      en: "The oldest one here, and the one that remembers the most. In exchange it is slow, and it spins down to rest the moment it can.",
    },
    personality: HDD_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "ssd",
    family: "pc",
    name: { ja: "SSDくん", en: "Mr. SSD" },
    motif: { ja: "SSD（ラベル面が顔）", en: "An SSD — the label is its face" },
    trait: { ja: "せっかち・若者", en: "Impatient, and young" },
    why: {
      ja: "待たされるのが何より苦手。HDDくんより桁違いに速いが、そのぶん容量では敵わない。",
      en: "Waiting is the one thing it cannot stand. Orders of magnitude faster than HDD — but it cannot match him on capacity.",
    },
    personality: SSD_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "mouse",
    family: "pc",
    name: { ja: "マウスくん", en: "Mr. Mouse" },
    motif: { ja: "マウス（本体の面が顔）", en: "A mouse — its shell is its face" },
    trait: { ja: "落ち着きない・小動物", en: "Restless, like a small animal" },
    why: {
      ja: "止まっている時間のほうが短い部品。少し動かされただけで反応してしまうので、じっとしていられない。",
      en: "A part that spends more time moving than still. The slightest nudge sets it off, so it never quite settles.",
    },
    personality: MOUSE_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "keyboard",
    family: "pc",
    name: { ja: "キーボードくん", en: "Mr. Keyboard" },
    motif: { ja: "キーボード（中央の面が顔）", en: "A keyboard — the centre plate is its face" },
    trait: { ja: "おしゃべり", en: "Talkative" },
    why: {
      ja: "叩かれるたびに音を返す仕事なので、黙っていられない。動きも細かく、いつも何か言っている。",
      en: "Its whole job is answering back every time it is struck, so silence is not in it. Its movements are small and constant.",
    },
    personality: KEYBOARD_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "monitor",
    family: "pc",
    name: { ja: "モニターくん", en: "Mr. Monitor" },
    motif: { ja: "ディスプレイ（画面が顔）", en: "A display — the screen is its face" },
    trait: { ja: "表情豊か・素直", en: "Expressive and honest" },
    why: {
      ja: "画面がそのまま顔なので、隠しごとができない。中で起きていることが全部そのまま出てしまう。",
      en: "Its screen is its face, so it cannot hide anything. Whatever happens inside shows up on it immediately.",
    },
    personality: MONITOR_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "arcade",
    family: "game",
    name: { ja: "アーケード筐体くん", en: "Mr. Arcade Cabinet" },
    motif: { ja: "ゲームセンターの筐体（画面が顔）", en: "An arcade cabinet — the screen is its face" },
    trait: { ja: "呼び込み役・にぎやか", en: "Loud, and always calling people over" },
    why: {
      ja: "自分から出向くのではなく、人が来るのを待つ側。だからその場を動かず、看板を光らせている。",
      en: "It does not go to people; people come to it. So it stays where it is and keeps its marquee lit.",
    },
    personality: ARCADE_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
  {
    id: "handheld",
    family: "game",
    name: { ja: "携帯機くん", en: "Mr. Handheld" },
    motif: { ja: "携帯ゲーム機（画面が顔）", en: "A handheld console — the screen is its face" },
    trait: { ja: "身軽・どこへでも付いてくる", en: "Light on its feet, and always tagging along" },
    why: {
      ja: "据え置きと違って持ち出される前提の子。だから身軽で、あちこちに現れる。",
      en: "Unlike the ones that stay put, it was made to be carried out. So it travels light and turns up everywhere.",
    },
    personality: HANDHELD_UNIT,
    disclaimer: COMMON_DISCLAIMER,
  },
];

export function findCharacter(id: string): Character | undefined {
  return CHARACTERS.find((c) => c.id === id);
}

/**
 * そのページに出すキャラを1体選ぶ。
 *
 * PCパーツの記事にアーケード筐体が立っていると、世界観の説明が要らない代わりに
 * 「何のサイトか」が薄れる。**ページの種類に合う系統から選ぶ。**
 * ただし完全に固定すると同じ子ばかり出るので、合う系統の中では毎回ランダムにする。
 */
export function pickCharacterFor(pathname: string, enabled: (id: CharacterId) => boolean): Character | null {
  const pool = CHARACTERS.filter((c) => enabled(c.id));
  if (pool.length === 0) return null;

  const isGamePage = /^\/(steam|game|mhp3|arma3|lowmoor-ja|apk-download|ppsspp|minecraft)(\/|$)/.test(pathname);
  const isPartsPage = /^\/(pc-parts|side-parts|bto-guide|compare|labs)(\/|$)/.test(pathname);

  let wanted: Character[] = pool;
  if (isGamePage) wanted = pool.filter((c) => c.family === "game");
  else if (isPartsPage) wanted = pool.filter((c) => c.family === "pc");
  // 記事ページやトップは種類が混ざるので、系統で絞らない
  if (wanted.length === 0) wanted = pool;

  return wanted[Math.floor(Math.random() * wanted.length)];
}
