# テストの記録（センサーログ）段 2

ブラウザのベンチマークやストレステストを動かしている間のセンサーを、PC の中だけで記録して、手元で要約し、
「AIに渡す用」の文書にする。仕様は `ツール群の仕様書.md` の 11〜14 章。点検の窓の「テストの記録」の面（`Features.TestRecording`）。

- **管理者もドライバも要らない。通信しない。** ドライバ（WinRing0・PawnIO など）・ファン／電圧／OC の操作・保護の解除・HWiNFO の共有メモリは入れていない
- 押すまで何も読まない。窓を閉じると記録は止まり、ここまでの分が保存される
- 置き場は `ドキュメント\PartsLife\runs\`（ドキュメントが付け替えられていても従う）。空きが 200 MB 未満なら始めない。12 時間で自動で止まる

## 読むもの・読まないもの

| 項目 | 読み方 | 備考 |
|---|---|---|
| CPU 使用率（全体・論理プロセッサごと） | PDH `Processor Information` | |
| CPU 実効クロック（全体・論理プロセッサごと） | 基準クロック × `% Processor Performance` | ブースト込み。特定コアの実測ではない |
| CPU 性能の制限・制限の印 | PDH `% Performance Limit`・`Performance Limit Flags` | OS の報告。印の意味は解読していない |
| CPU パッケージ電力 | PDH `Energy Meter` の `*_PKG`（mW → W） | Windows が電力メーターを出す PC だけ。0.5〜1500 W を外れたら使わない。OS 報告で推定を含みうる |
| メモリ・ディスク・電源 | `GlobalMemoryStatusEx`・PDH `PhysicalDisk(_Total)`・`GetSystemPowerStatus` | 電源プラン（種類だけ。名前は取らない）は記録の先頭に |
| GPU 使用率・専用メモリ | NVIDIA は NVML、それ以外は PDH `GPU Engine`・`GPU Adapter Memory` | |
| NVIDIA の温度・クロック・メモリクロック・電力・電力上限・ファン・制限の理由 | ドライバ同梱の `nvml.dll`（読み出しの関数だけ） | 制限の理由は `nvmlClocksEventReasons` の bit |
| AMD の温度・ホットスポット・クロック・メモリクロック・電力・ファン（rpm） | ドライバ同梱の `amdadlx64.dll`（ADLX。性能の監視の読み出しだけ） | 名前で GPU を結ぶ。同名が複数で見分けられなければ読まない |
| **CPU の温度・コア電圧・ファン・マザーボードの温度** | **読まない（`needs-extension`）** | カーネルドライバが要る。段 3 |
| **Intel の GPU の温度・クロック・電力・ファン** | **読まない（`vendor-not-implemented`）** | IGCL は段 3 以降。使用率とメモリだけ読める |

読めない項目は、開始のときに「読めます／読めません／追加機能が必要」と理由つきで見せ、記録の列にも入れない（空のまま）。
値を推測で埋めることはしない。

GPU の番号（`gpu0`…）は **PCI のバス番号順**。名前や NVML の番号では決めない（同じ型番が 2 枚でも、NVML の並びがどうでも同じ向きに並ぶ。
NVML と DXGI は PCI の番地で突き合わせている）。

### この PC での確認（2026-10-18、管理者なし）

Ryzen 9 7950X（32 論理）・RTX 5070（gpu0）・RTX 3080 Ti（gpu1）・内蔵 Radeon（gpu2）。

- 読めた: CPU 使用率・クロック・論理プロセッサごと・制限・パッケージ電力、メモリ、ディスク、電源（AC）、gpu0/gpu1 は 9 項目すべて（NVML）、
  gpu2 は使用率・メモリ（PDH）と温度・クロック・メモリクロック・電力（ADLX）
- 読めなかった: gpu2 のホットスポットとファン（内蔵のため非対応）、電池の残り（電池なし）
- 追加機能が必要: CPU 温度・コア電圧・ファン・マザーボード温度
- パッケージ電力の単位は、同じカウンタ集合の Energy（pWh）と Time（ms）の差分から出した電力と突き合わせて mW だと確かめた

## ファイルの形

### `run-<ID>.json`（記録の見出し）

`<ID>` は 16 文字の小文字と数字（推測できない乱数）。ファイル名と中身の `runId` が一致する。**形を変えるときは `formatVersion` を上げる**
（項目の追加は同じ版のまま。読む側は知らない項目を無視する）。UTF-8（BOM なし）。

| 項目 | 中身 |
|---|---|
| `format` / `formatVersion` | `"partslife-run"` / `1` |
| `app.version` | PartsLife の版 |
| `runId` | 実行の ID（手元だけ。**投稿には使わない**。投稿用は別に発行する） |
| `status` / `stopReason` | `recording`（記録中・落ちた）/ `completed` / `interrupted`、`user`・`max-duration`・`io-error`・`window-closed` など |
| `startUtc` / `endUtc` | ISO 8601（ミリ秒・`Z`）。記録中は `endUtc` が null |
| `intervalMs` | 1000 |
| `system` | `os`・`osBuild`・`powerPlan`（`balanced` など種類だけ）・`ramMb`・`logicalProcessors` |
| `build` | `code`（構成コード `PCB1.…`）と `config`（その中身の JSON）。読めなければ null |
| `gpus[]` | `id`（`gpu0`）・`name`・`vendor`（`nvidia`/`amd`/`intel`/`other`）・`vramMb` |
| `capabilities[]` | `key`・`unit`・`source`・`state`（`available`/`unavailable`/`needs-extension`）・`reason` |
| `stream` | `file`（`sensors-<ID>.csv`）・`format: "csv"`・`columns[]`（先頭は `t_s`, `utc`）・`monotonicColumn`・`utcColumn` |
| `markers[]` | `kind`（`bench-start` `bench-end` `stress-start` `stress-end` `mark` の 5 つだけ）・`tSec`・`utc` |
| `stats` | `samples`・`gapSec`・`maxGapSec`・`sourceTimeouts{}` |
| `summary` | 止めたあとの要約（下記）。止める前は null |

PC の名前・利用者名・シリアル番号・ファイルのパスは入らない（持つ場所が無い。試験で確かめている）。

### `sensors-<ID>.csv`（毎秒の値）

1 行目が列名。`t_s` = 記録の開始からの秒（**単調な時計**。時刻の変更に影響されない）、`utc` = その行を読んだときの UTC（ISO 8601）。
以降は `stream.columns` の順。小数点はピリオド・区切りはカンマ・読めなかった値は空欄・UTF-8（BOM なし）。1 行ずつ書いて OS に渡すので、途中で落ちてもそこまでが残る。
間隔に遅れたときは、取りこぼした秒を詰め込まず、`t_s` が飛ぶ（記録の穴として残る）。

列の名前（`gpuN.` の N は上の番号）:

| 列 | 単位 | | 列 | 単位 |
|---|---|---|---|---|
| `cpu.load_pct` | % | | `gpuN.util_pct` | % |
| `cpu.clock_mhz` | MHz | | `gpuN.mem_used_mb` | MB |
| `cpu.perf_limit_pct` | % | | `gpuN.temp_c` / `gpuN.hotspot_c` | ℃ |
| `cpu.perf_limit_flags` | bit | | `gpuN.clock_mhz` / `gpuN.mem_clock_mhz` | MHz |
| `cpu.pkg_power_w` | W | | `gpuN.power_w` / `gpuN.power_limit_w` | W |
| `cpu.lpK.load_pct` / `cpu.lpK.clock_mhz` | % / MHz | | `gpuN.fan_pct`（NVIDIA）/ `gpuN.fan_rpm`（AMD） | % / rpm |
| `mem.used_pct` / `mem.used_mb` | % / MB | | `gpuN.throttle` | bit（NVML の制限の理由） |
| `disk.read_mbps` / `disk.write_mbps` / `disk.busy_pct` | MB/s / % | | `power.ac` / `power.battery_pct` | 1=AC・0=電池 / % |

## 要約の規則（止めたとき、手元で）

- 項目ごとに最大・平均・**持続した値**（負荷が高かった最後の N 秒の、負荷が高かった時点だけの中央値。N は記録の長さの 1/3、30〜300 秒。60 秒未満の記録では出さない）
- 制限の印: GPU は NVML の bit を名前にして秒数（うち高負荷中）、CPU は OS の印が立っていた秒数。**印が立っていたことを言うだけで、故障とは言わない**
- 温度が上がりながらクロックが下がっていた時間帯: 30 秒の窓で温度 +3 ℃・クロック −5% 以上・負荷 50% 以上。**同時に起きただけで原因は言わない**
- 目印で区切った区間（ベンチ・ストレス）ごとの最大・平均・中央値
- 論理プロセッサごとの平均使用率の散らばり（単一スレッドか全体か）
- 注記: 読めなかった項目・ほとんど使われなかった GPU（使用率の最大 20% 未満）・記録の穴・読み取りが戻らなかった回数・電池駆動の時間・CPU の温度が読めないのでクロック低下の理由を切り分けられない、など

規則は記録のファイル（CSV）から計算し直せるので、規則を直せば古い記録の要約も良くなる（過去の記録を開くと計算し直す）。

## AI に渡す用の書き出し

許可した項目を 1 つずつ書く（記録の見出しを写さない）。入る物: 版・記録の長さ・OS の名前と版・電源プラン・メモリ量・CPU と GPU の型番・メモリの構成・
項目の要約・制限の印・同時に起きた時間帯・目印の種類と時刻（開始からの秒）・間引いた時間の変化（最大 48 点）・注記。
**入らない物**: シリアル番号・PC の名前・利用者名・ファイルのパス・プロセス名・自由に書いた文字・実行の ID・日付（UTC を含む）・構成コード・SSD やメモリの型番。

- 名前は PC の名前・利用者名・パス・シリアルに見えたら書き出さない。出来上がった全文も最後に確かめ、紛れていたら**何も出さずに止める**
- 前半は AI への説明（項目の意味・「観測／仮説／追加で確認すること」に分けて答える頼み・**故障の断定をしない・電圧の安全の保証や昇圧・保護の解除の指示をしない**）、後半が JSON
- 画面にプレビューを出し、**そのままの内容**をコピー・保存する。送信はしない

## ブラウザ側が `run-<ID>.json` を取り込む（段 3 の連携の前の、ファイルでの受け渡し）

1. 利用者が `run-<ID>.json` と `sensors-<ID>.csv` を選ぶ（ファイル選択かドロップ。**サーバーへは送らない**）。ブラウザの中で両方を読む
2. 検査: `format === "partslife-run"`・`formatVersion` が対応する版以下・`runId` が `^[a-z0-9]{8,40}$`・CSV の 1〜2 列目が `t_s`, `utc`・`stream.file` が選んだ CSV の名前と同じ。
   知らない項目は無視する。`status !== "completed"` は「途中で終わった記録」と表示する
3. 結び付け: 手元の `runId` を鍵に、`build.code`（構成コード）・ベンチの結果・センサーのログ・解析の結果を IndexedDB に結ぶ
   （「実行の ID → 構成の版 → ベンチの結果／センサーのログ／FPS ログ／解析の結果」）。`build.code` はマイPC の登録と同じ PCB1 なので、そのまま構成の版に結べる
4. 時刻合わせ: ブラウザのベンチの開始・終了の UTC（`performance.timeOrigin + performance.now()`）と CSV の `utc` を突き合わせる。同じ PC の時計なので揃う。
   利用者が押した `markers[]`（`bench-start`/`bench-end` など）があれば、それを区間の正とする。段 3 の自動連携では、開始・停止の印を直接受け取るので、この手作業が要らなくなる
5. 使った GPU の確認: WebGPU のアダプタ名と `gpus[].name`、`summary` の `gpuN.util_pct` の最大（使われた GPU は高い）で判断する。名前だけでは決めない
6. 投稿するとき: **`runId` は公開しない**。投稿用の ID を別に発行する。サーバーはセンサーのログを受け付けない（1 件 16KB まで）ので、投稿に入れるのは点数と最小限の条件だけ

## 段 3 に残すもの

- CPU の温度・コア電圧・ファン・マザーボードの電圧／温度（LibreHardwareMonitor ＋ PawnIO を別プロセスに分けて。必要な権限だけ・版を固定・ライセンス条件を満たす）。
  この版では `needs-extension` と答えるだけの読み口（`ExtensionSource`）が席を取ってあり、列の名前（`cpu.temp_c` `cpu.vcore_v` `board.fan_rpm` `board.temp_c`）も決めてある
- Intel の GPU（IGCL）。`GpuGapSource` が理由つきで「読めない」と答えている。読み口を足すときは `ISensorSource` を 1 つ実装して `DefaultSources` に足す
- 同じ PC の中だけで通信する自動連携（開始・停止の印と監視の情報だけ。切れたら負荷を止める）
- サーバーの Claude での解析（認証・回数の枠・トークンと 1 日の費用の上限つき）
- AMD の同名 GPU が複数あるときの見分け（ADLX から PCI の番地が引けるか、実機で確かめる）
