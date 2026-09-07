import type { CharacterId } from "./characters";

/**
 * キャラ同士が出会ったときに見せる「規格と相性」。
 *
 * ■なぜ実在の規格で判定するのか
 * このサイトの読者は自作PCを見に来ている。適当な相性表を出せば一目で嘘と分かるし、
 * 逆に**本当の判定基準を見せれば、それ自体が読み物になる**。
 *
 * ■組み合わせごとに規則を持つ
 * 「クーラーとCPU」と「メモリとマザー」では見るところが違う。
 * 1つの汎用ルールに押し込めると、どちらも嘘になる。
 * 規格の値は `Spec` に緩く持たせ、**何を見るかは規則の側が決める**。
 */

export type Verdict = "ok" | "warn" | "ng";

/**
 * 規格の値。組み合わせによって見る項目が違うので、必要なものだけ入れる。
 * 表示に使う文言は規則(show)が組み立てるので、ここには入れない。
 */
export type Spec = {
  /** ソケット。クーラー・マザーは複数、CPUは1つ */
  sockets?: string[];
  /** クーラー: 冷やせるW / CPU: TDP / GPU: 推奨電源W / 電源: 容量W */
  watts?: number;
  /** メモリ・マザーの世代 */
  ddr?: 4 | 5;
  /** M.2 の世代。0 は SATA 接続 */
  m2?: 0 | 3 | 4 | 5;
  /** 映像端子の系統。USBC は DisplayPort Alt Mode での出力を指す */
  video?: ("DP" | "HDMI" | "USBC")[];
  /** 画面の縦の画素数と、リフレッシュレート */
  lines?: number;
  hz?: number;
  /** メモリの動作クロック / CPUが定格で対応する上限 */
  speed?: number;
  /** チップセットや型番など、表示用の短い名前 */
  name?: string;
};

export type CompatResult = {
  verdict: Verdict;
  /** なぜその判定なのか。1行で読める長さにする */
  reason: { ja: string; en: string };
};

/**
 * 取付穴が共通のソケット群。**同じ群なら同じクーラーがそのまま付く。**
 *   intel-new … LGA1700 と LGA1851 は取付穴が同じ
 *   amd-am    … AM4 と AM5 は取付穴が同じ（AMDが意図的に揃えている）
 *   intel-old … LGA115x/1200 は75mm角で共通
 */
const MOUNT_GROUP: Record<string, string> = {
  LGA1700: "intel-new",
  LGA1851: "intel-new",
  LGA1200: "intel-old",
  LGA1151: "intel-old",
  AM4: "amd-am",
  AM5: "amd-am",
};

const worse = (a: Verdict, b: Verdict): Verdict =>
  a === "ng" || b === "ng" ? "ng" : a === "warn" || b === "warn" ? "warn" : "ok";

const R = (verdict: Verdict, ja: string, en: string): CompatResult => ({ verdict, reason: { ja, en } });

// ---------------------------------------------------------------------------
// キャラごとの「個体」。出会うたびに1つ選ぶので毎回同じ結果にならない。
// ---------------------------------------------------------------------------
export const SPECS: Partial<Record<CharacterId, Spec[]>> = {
  "cpu-cooler": [
    { sockets: ["LGA1700", "LGA1851"], watts: 150 },
    { sockets: ["AM4", "AM5"], watts: 230 },
    { sockets: ["LGA1200"], watts: 95 },
    { sockets: ["LGA1700", "LGA1851"], watts: 260 },
    { sockets: ["AM4", "AM5"], watts: 105 },
  ],
  cpu: [
    { sockets: ["LGA1851"], watts: 125, ddr: 5, speed: 6400 },
    { sockets: ["LGA1700"], watts: 65, ddr: 5, speed: 5600 },
    { sockets: ["AM5"], watts: 170, ddr: 5, speed: 5200 },
    { sockets: ["AM4"], watts: 105, ddr: 4, speed: 3200 },
    { sockets: ["LGA1200"], watts: 125, ddr: 4, speed: 2933 },
  ],
  motherboard: [
    { sockets: ["LGA1851"], ddr: 5, m2: 5, name: "Z890" },
    { sockets: ["LGA1700"], ddr: 5, m2: 4, name: "Z790" },
    { sockets: ["LGA1700"], ddr: 4, m2: 4, name: "B660" },
    { sockets: ["AM5"], ddr: 5, m2: 5, name: "B650" },
    { sockets: ["AM4"], ddr: 4, m2: 4, name: "B550" },
  ],
  memory: [
    { ddr: 5, speed: 6000, name: "DDR5-6000" },
    { ddr: 5, speed: 5600, name: "DDR5-5600" },
    { ddr: 4, speed: 3200, name: "DDR4-3200" },
    { ddr: 4, speed: 2666, name: "DDR4-2666" },
  ],
  gpu: [
    { watts: 850, video: ["DP", "HDMI"], name: "上位カード" },
    { watts: 650, video: ["DP", "HDMI"], name: "中位カード" },
    { watts: 450, video: ["HDMI"], name: "省電力カード" },
    { watts: 1000, video: ["DP", "HDMI"], name: "最上位カード" },
  ],
  ssd: [
    { m2: 5, name: "Gen5 NVMe" },
    { m2: 4, name: "Gen4 NVMe" },
    { m2: 3, name: "Gen3 NVMe" },
    { m2: 0, name: "SATA" },
  ],
  monitor: [
    { lines: 2160, hz: 144, video: ["DP", "HDMI", "USBC"], name: "4K 144Hz" },
    { lines: 1440, hz: 165, video: ["DP", "HDMI"], name: "WQHD 165Hz" },
    { lines: 1080, hz: 60, video: ["HDMI"], name: "フルHD 60Hz" },
    { lines: 2160, hz: 60, video: ["DP"], name: "4K 60Hz(DPのみ)" },
  ],
  handheld: [
    { video: ["USBC"], name: "USB-C 映像出力あり" },
    { video: ["HDMI"], name: "ドック(HDMI)付き" },
    { video: [], name: "映像出力なし" },
  ],
  psu: [
    { watts: 850 },
    { watts: 650 },
    { watts: 500 },
    { watts: 1200 },
  ],
};

// ---------------------------------------------------------------------------
// 組み合わせごとの規則
// ---------------------------------------------------------------------------
type Rule = {
  a: CharacterId;
  b: CharacterId;
  /** 左右に出す規格の文字列 */
  show: (a: Spec, b: Spec) => [string, string];
  judge: (a: Spec, b: Spec) => CompatResult;
};

const RULES: Rule[] = [
  {
    // クーラー × CPU: 取り付けと冷却の2軸を見て、悪い方を採る。
    // 「ソケットは合うが冷やしきれない」は実際によくある失敗なので、
    // 片方だけで ○× を出すと嘘になる。
    a: "cpu-cooler",
    b: "cpu",
    show: (c, p) => [`${c.sockets!.join("/")} · ${c.watts}Wまで`, `${p.sockets![0]} · ${p.watts}W`],
    judge: (c, p) => {
      const socket = p.sockets![0];
      const fits = c.sockets!.includes(socket)
        || c.sockets!.some((s) => MOUNT_GROUP[s] && MOUNT_GROUP[s] === MOUNT_GROUP[socket]);
      const mount: Verdict = fits ? "ok" : MOUNT_GROUP[socket] ? "warn" : "ng";
      const thermal: Verdict = c.watts! < p.watts! ? "ng" : c.watts! < p.watts! * 1.15 ? "warn" : "ok";
      if (mount === "warn") return R(worse(mount, thermal), "別売りの取付金具が要る", "Needs a separately sold bracket");
      if (thermal === "ng") return R("ng", "取り付けは合うが、冷やしきれない", "It mounts, but cannot keep up with the heat");
      if (thermal === "warn") return R("warn", "付くが、冷却の余裕が少ない", "It fits, but the thermal headroom is thin");
      return R("ok", "そのまま付く。余裕もある", "Fits as-is, with headroom to spare");
    },
  },
  {
    // CPU × マザー: ソケットが一致して初めて挿さる。
    // 同じソケットでも世代が違えばBIOS更新が要ることがあるので、そこは△にしない
    // （型番まで見ないと決められないため、断定を避けて○にとどめる）。
    a: "cpu",
    b: "motherboard",
    show: (p, m) => [`${p.sockets![0]} · ${p.watts}W`, `${m.name} · ${m.sockets![0]}`],
    judge: (p, m) => {
      if (m.sockets![0] === p.sockets![0]) return R("ok", "ソケットが同じ。そのまま挿さる", "Same socket — it drops right in");
      if (MOUNT_GROUP[m.sockets![0]] === MOUNT_GROUP[p.sockets![0]]) {
        return R("warn", "取付穴は同じだが、ソケットが違うので挿さらない", "Same mounting holes, but the socket differs — it will not seat");
      }
      return R("ng", "ソケットが違う。挿さらない", "Different socket — it will not fit");
    },
  },
  {
    // メモリ × マザー: DDR4 と DDR5 は切り欠きの位置が違い、物理的に挿さらない。
    a: "memory",
    b: "motherboard",
    show: (r, m) => [`${r.name}`, `${m.name} · DDR${m.ddr}`],
    judge: (r, m) =>
      r.ddr === m.ddr
        ? R("ok", "世代が同じ。そのまま挿さる", "Same generation — it drops right in")
        : R("ng", `DDR${r.ddr} と DDR${m.ddr} は切り欠きが違う。挿さらない`, `DDR${r.ddr} and DDR${m.ddr} are keyed differently — it will not fit`),
  },
  {
    // GPU × 電源: カードの推奨電源容量と、電源の容量を比べる。
    a: "gpu",
    b: "psu",
    show: (g, p) => [`${g.name} · 推奨 ${g.watts}W`, `${p.watts}W`],
    judge: (g, p) => {
      if (p.watts! >= g.watts!) return R("ok", "容量に余裕がある", "The supply has room to spare");
      if (p.watts! >= g.watts! * 0.85) return R("warn", "動くが、容量の余裕が少ない", "It runs, but the headroom is thin");
      return R("ng", "容量が足りない。負荷時に落ちる", "Not enough capacity — it will cut out under load");
    },
  },
  {
    // メモリ × CPU: **挿さるかどうかはマザーの話**なので、ここは
    // 「そのCPUが定格で回せるクロックか」を見る。
    // 定格を超えていても XMP/EXPO で動くのが普通なので ✕ にはしない。
    a: "memory",
    b: "cpu",
    show: (r, p) => [`${r.name}`, `${p.sockets![0]} · 定格 DDR${p.ddr}-${p.speed}`],
    judge: (r, p) => {
      if (r.ddr !== p.ddr) {
        return R("ng", `DDR${r.ddr} と DDR${p.ddr} で世代が違う`, `DDR${r.ddr} against DDR${p.ddr} — different generations`);
      }
      if (r.speed! <= p.speed!) return R("ok", "定格の範囲。そのまま動く", "Within the rated speed — it just works");
      return R("warn", "定格より速い。XMP/EXPO を入れて動かす前提", "Faster than rated — it needs XMP/EXPO enabled");
    },
  },
  {
    // SSD × マザー: M.2 は下位互換なので「挿さらない」はまず起きない。
    // 起きるのは**本来の速度が出ない**方なので、そこを △ にする。
    a: "ssd",
    b: "motherboard",
    show: (d, m) => [`${d.name}`, `${m.name} · M.2 Gen${m.m2}`],
    judge: (d, m) => {
      if (d.m2 === 0) return R("ok", "SATAで繋がる。速度は控えめ", "Connects over SATA — modest speed, but it works");
      if (m.m2! >= d.m2!) return R("ok", "本来の速度が出る", "It runs at its rated speed");
      return R("warn", `Gen${m.m2} 止まりになる。挿さるが本来の速度は出ない`, `Capped at Gen${m.m2} — it fits, but not at full speed`);
    },
  },
  {
    // モニター × GPU: 端子が無ければ映らない（そこだけが ✕）。
    // 端子が合えば**表示自体はできる**ので、力不足は ✕ ではなく △ にする。
    // ここを ✕ にすると「繋いでも映らない」という嘘になる。
    a: "monitor",
    b: "gpu",
    show: (mo, g) => [`${mo.name} · ${mo.video!.join("/")}`, `${g.name} · ${g.video!.join("/")}`],
    judge: (mo, g) => {
      const common = mo.video!.filter((v) => g.video!.includes(v));
      if (common.length === 0) {
        return R("ng", "共通の映像端子が無い。変換器が要る", "No shared video port — you would need an adapter");
      }
      const demand = (mo.lines! / 1080) * (mo.hz! / 60);
      const capacity = g.watts! / 450;
      if (capacity >= demand) return R("ok", "この画質でも余裕がある", "Plenty of headroom at this resolution");
      if (capacity >= demand * 0.55) return R("warn", "映るが、設定を落とさないとこの滑らかさは出ない", "It displays fine, but you must lower settings to hold this refresh rate");
      return R("warn", "映りはする。ただしゲームでこの画質は荷が重い", "It displays, but this resolution is too much for it in games");
    },
  },
  {
    // モニター × 携帯機: 携帯機を外の画面に映せるか。
    // USB-C の映像出力(DisplayPort Alt Mode)に対応した機種なら、
    // 対応モニターへ1本で繋がる。ドック付きなら HDMI で繋がる。
    a: "monitor",
    b: "handheld",
    show: (mo, h) => [
      `${mo.name} · ${mo.video!.join("/")}`,
      h.video!.length ? `${h.name}` : "映像出力なし",
    ],
    judge: (mo, h) => {
      if (h.video!.length === 0) {
        return R("ng", "この携帯機は外の画面に映せない", "This handheld cannot drive an external display");
      }
      if (h.video!.some((v) => mo.video!.includes(v))) {
        return R("ok", "1本で繋がる", "A single cable does it");
      }
      return R("warn", "変換器かドックを挟めば映せる", "It works through an adapter or dock");
    },
  },
];

function findRule(a: CharacterId, b: CharacterId): { rule: Rule; swap: boolean } | null {
  for (const rule of RULES) {
    if (rule.a === a && rule.b === b) return { rule, swap: false };
    if (rule.a === b && rule.b === a) return { rule, swap: true };
  }
  return null;
}

/** その2キャラの相性を判定できるか（会話の相手を選ぶときに使う）。 */
export function hasCompat(a: CharacterId, b: CharacterId): boolean {
  return findRule(a, b) !== null;
}

/** 相性を判定できる相手の一覧。 */
export function partnersOf(a: CharacterId): CharacterId[] {
  const out: CharacterId[] = [];
  for (const rule of RULES) {
    if (rule.a === a) out.push(rule.b);
    else if (rule.b === a) out.push(rule.a);
  }
  return out;
}

export type Encounter = {
  /** a 側（呼び出しで先に渡したキャラ）の表示 */
  aText: string;
  bText: string;
  result: CompatResult;
};

/**
 * 出会い1回ぶんの組み合わせを選ぶ。
 *
 * **素直に両側をランダムに選ぶと △ ばかりになる**（実測で3回連続 △）。
 * 規格の組み合わせは「合わない方が多い」のが現実なので、そのままだと
 * ○ と ✕ がほとんど出ず、印が3種類ある意味が失われる。
 * そこで**先に出したい判定を重みで決め、それになる組み合わせから選ぶ**。
 * 出てくる組み合わせ自体は実在の規格どおりなので、嘘にはならない。
 */
export function pickEncounter(aId: CharacterId, bId: CharacterId): Encounter | null {
  const found = findRule(aId, bId);
  if (!found) return null;
  const { rule, swap } = found;
  const leftId = swap ? bId : aId;
  const rightId = swap ? aId : bId;
  const L = SPECS[leftId];
  const Rr = SPECS[rightId];
  if (!L || !Rr) return null;

  const all: Encounter[] = [];
  for (const l of L) {
    for (const r of Rr) {
      const [lt, rt] = rule.show(l, r);
      const result = rule.judge(l, r);
      all.push(swap ? { aText: rt, bText: lt, result } : { aText: lt, bText: rt, result });
    }
  }
  if (all.length === 0) return null;

  const roll = Math.random();
  const want: Verdict = roll < 0.5 ? "ok" : roll < 0.8 ? "warn" : "ng";
  const pool = all.filter((x) => x.result.verdict === want);
  const use = pool.length > 0 ? pool : all;
  return use[Math.floor(Math.random() * use.length)];
}
