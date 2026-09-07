# sprites — ドット絵の正

ここが**キャラクターの絵と動きの唯一の正**。
サイト(`indie-pc-portal`)側の `src/lib/mascot/` へは
`tools/sync-to-site.sh` で一方向に流す。**逆向きに直さないこと。**
両方で直すと必ずズレる。

- `palette.ts` … 色
- `sprite.ts` … 共通の描画（手足・顔・輪郭・変形・ポーズ合成）
- `bodies.ts` … キャラごとの体の形
- `motions.ts` … 動きの定義
- `marks.ts` … zzz / ‼ / ♪ / ○△✕
- `characters.ts` `personality.ts` `compat.ts` … 設定・性格・相性

DOM に触るファイル（`sheet.ts` 等）はここには置かない。
Node からも C# 用の書き出しからも使えるよう、**純粋な計算だけ**にしておく。
