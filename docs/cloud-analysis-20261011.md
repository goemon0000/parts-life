## 要約（2026-10-11 クラウド解析）

- **ブランチ** `cloud/analysis-20261011`（main・タグ・Release・PR には触れていない）。コミット: `e59e15e` タブの対応表＋試験 / `32d16cb` 窓の上のタブ（試作） / `bc1fd31` GPU カウンタ名の読み取り（正規表現なし＋覚えておく） / 本書
- **ビルド・試験（Linux, .NET 10.0.112）**: `dotnet build PartsLife.slnx` 成功・警告 0。Core.Tests 113/113（+34）・Update.Tests 142/142 成功。Checks.Tests は WindowsDesktop の実行環境が無く Linux では回せない
- **上位 10 件**
  1. `Rendering/BoardRenderer.cs:297` `Fill` が長方形ごとに未凍結の Brush を新しく作る → 毎秒 500〜800 個（凍結済み Brush を色で使い回す）
  2. `MainWindow.xaml.cs:55` コマ送り 140 ms 固定。メモリくんは常に `idle`（140 ms のコマ）なので盤は放置でも約 7 fps で描き直し → ロック中・画面オフで止める
  3. `Sensors/PdhSampler.cs:110-160` GPU Engine の全インスタンスに Regex（**実施済み** `bc1fd31`）
  4. `MainWindow.xaml.cs:799-865` 数値欄を 0.5 秒ごとに丸ごと作り直す（行を使い回す）。`:359` 展開中の PDH 0.5 秒は注記「1 秒に 1 回」と食い違う
  5. `MainWindow.xaml.cs:180-190` `Guarded` の失敗が累計 61 回で描画も計測も黙って止まる（数が戻らない）
  6. `Core/Game/PartyState.cs:388` 保存の `catch {}` で保存が止まっても気づけない
  7. `Sensors/DiskSampler.cs:48` 記憶装置の種類は起動時 1 回だけ → 後から繋いだ SSD が HDD くんになる
  8. `Checks/Protocol/HelperSession.cs:185-190` 止めたときの 10 秒の打ち切りが待ち中の読み取りに効かない／`Scan/SurfaceScanner.cs:117` 同期 ReadFile で stop が効かない
  9. `Checks/SystemInfo/SystemCheck.cs:71-155`・`Build/BuildCollector.cs` WMI に時間切れが無く、ManagementObject を Dispose しない／`:123` 切れたネットワークドライブで止まる
  10. `Checks/SystemInfo/EventCounts.cs:22` `0x100` は TolerateQueryErrors ではない（正しくは `0x1000`）
- **タブ**: 窓の上に「ようす / 表示 / 起動・更新 / 点検 / データ」。選んだときだけ下に設定の欄を開き、もう一度・Esc で畳む。献立もタブも `MenuMap` の 1 つの表から組み立て、同じ `Execute` を通る。右クリックの献立は v0.7.3 と同じ並び・文言で残す。保存の形は変えない（移行不要）。タブが開いている間は更新の入れ替えをしない。Ctrl+1〜5・RadioButton/CheckBox で読み上げ対応
- **Windows で確かめること**: Checks.Tests を含む全試験と publish／タブの各項目が献立と同じに効く（前面・言語・名札・起動時・更新 3 設定と今すぐ確認・点検・最初からの確認）／右クリックの献立が v0.7.3 と同じ／タブを開いている間は入れ替えが起きない／125・150%・倍率違いの 2 画面・ハイコントラスト・Narrator／`--probe` の GPU 行が v0.7.3 と同じ・`--bench sense` で前後比較／広告値（約 1.8% / 118 MB）の測り直し（詳細は 6.3）

---

# パーツぐらし 解析と改善提案（2026-10-11・クラウド）

対象: main `02dd0bd`（v0.7.3）。ブランチ `cloud/analysis-20261011`。
Windows / WPF は動かせない環境（Linux）での**静的解析＋Linux でのビルド・試験**。実行時の数値（CPU% / MB）は測っていない。
影響の見積もりは「常駐して放っておいたとき（タブも数値欄も畳んだ姿）」を一番重く見ている。

目次

1. 全体の作り（プロジェクト・スレッドと時計・計測・描画・保存）
2. 無駄と最適化（file:line・なぜ効くか・見積もり・危うさ）
3. 不具合・脆い所
4. 合いそうな機能（合う度 × 手間の順）
5. 窓の上のタブ（設計と試作）
6. このブランチで入れた物と、Windows で確かめること

---

## 1. 全体の作り

### 1.1 プロジェクト

| プロジェクト | 中身 | 対象 | 常駐で読み込まれるか |
|---|---|---|---|
| `src/PartsLife` | 本体（WinExe・WPF）。窓・計測（Sensors）・描画（Rendering）・更新との繋ぎ（Updates） | net10.0-windows・単一 exe・自己完結・圧縮 | ○ |
| `src/PartsLife.Core` | 物語・レベル・冒険・保存（PartyState）・GPU の顔ぶれの決まり。WPF を持たない | net10.0 | ○ |
| `src/PartsLife.Update` | 自動更新の決まり（版の比較・Release の読み・SHA256・Authenticode・入れ替えと戻し） | net10.0 | ○（時計で確かめるとき） |
| `src/PartsLife.CheckWindow` | 点検の窓（WPF をコードで組む） | net10.0-windows | ×（献立で「点検」を押したとき。`MainWindow.OpenChecks` が NoInlining で境を作る） |
| `src/PartsLife.Checks` | 点検の中身（システムチェック・CPU/メモリ/GPU 負荷・記憶媒体・ビルドコード） | net10.0-windows | × |
| `src/PartsLife.Check` | 管理者の要る読み取りを受け持つ補助 exe（NativeAOT） | win-x64 AOT | ×（記憶媒体の点検で起こす） |
| `tests/*` | Core（Linux で回る）・Update（Linux で回る）・Checks（net10.0-windows。**Linux では WindowsDesktop の実行環境が無く回らない**） | | |

### 1.2 スレッドと時計（常駐の姿）

すべて UI スレッドの `DispatcherTimer`（WPF の合成スレッドは別）。

| 時計 | 間隔 | 場所 | 中身 |
|---|---|---|---|
| `_frameTimer` | 140 ms | `MainWindow.xaml.cs:55` | `Redraw()`：盤の指紋を取り、変わっていれば `DrawingVisual` に描き直す |
| `_sensorTimer` | 畳み 2 s / 展開 0.5 s | `MainWindow.xaml.cs:56`, `:359` | `OnSensorTick()`：CPU（GetSystemTimes・NtQuerySystemInformation）・メモリ・PDH（GPU Engine / Adapter Memory / PhysicalDisk）・30 秒ごとに DriveInfo・顔ぶれの組み立て・物語の Tick・60 秒ごとに保存・展開時は数値欄の作り直し |
| `_noticeTimer` | 任意 | `MainWindow.xaml.cs:31` | お知らせを畳む |
| 更新 `_first/_poll/_idle/_healthy` | 起動後の遅延・定期・1 分・無事の見届け | `Updates/UpdateController.cs:29-32` | 確かめ（`Task.Run`）→ 結果は UI スレッドで扱う |
| `DiskSampler._probe` | 起動時 1 回 | `Sensors/DiskSampler.cs:48` | WMI（MSFT_PhysicalDisk / MSFT_Partition）をスレッドプールで |

### 1.3 計測

- CPU 全体 `GetSystemTimes`、論理コア別 `NtQuerySystemInformation(8)`、メモリ `GlobalMemoryStatusEx`（`Sensors/SystemSampler.cs`）。管理者不要。
- GPU 使用率・VRAM・ディスク流量は 1 つの PDH クエリ（英語名で登録、`Sensors/PdhSampler.cs:262-281`）。GPU の顔ぶれは D3DKMT（`GpuIdentity.cs`）＋ Core の `GpuRoster`。
- 記憶装置の種類は WMI を起動時に 1 回、容量は `DriveInfo` を 30 秒に 1 回。
- 温度・ファンは扱わない（ドライバが要るため。README の方針どおり）。

### 1.4 描画

- 盤は `BoardSurface`（`FrameworkElement` に `DrawingVisual` 1 枚）へ描き、拡大は `ScaleTransform`＋最近傍。基板の下地だけ `RenderTargetBitmap` に 1 回焼いて貼る（`Rendering/BoardRenderer.cs:270-280`）。
- コマはアトラス（`assets/atlas.json`）の `ms` に従う。`idle` 140 ms・`walk` 120 ms・`sleep` 620 ms など。
- 文字（レベル・物語・数値欄）は WPF の `TextBlock`。数値欄は展開中、計測のたびに丸ごと作り直す。

### 1.5 保存

- `%APPDATA%\PartsLife\state.json`（`PartyState`）。60 秒ごと・閉じるとき・最初からのとき。tmp に書いて `Flush(true)` してから置き換え（`Core/Game/PartyState.cs:363-385`）。
- 読めない欄だけ既定値（`LenientJson`）、丸ごと読めなければ `.bad-日時` に退ける。知らない欄は `[JsonExtensionData]` で書き戻す（**新しい欄を足しても古い版で消えない**）。
- 更新の覚え書きは `%LOCALAPPDATA%\PartsLife\updates`（`PartsLife.Update` の `UpdateState`）。


---

## 2. 無駄と最適化

行番号は、本体（`src/PartsLife`）は **v0.7.3（02dd0bd）の行**、ほかは今の行。
「効き目」は常駐の放置の姿（畳んだまま）に効く大きさ。数値は静的な見積もりなので、Windows で `--bench draw|sense|nopdh` と PerfView / dotnet-counters（`gc-heap-size`・`gen-0-gc-count`・`alloc-rate`）で前後を測ること。

### 2.1 常駐の放置の姿に効く物（優先）

| # | 場所 | 何が起きているか | なぜ効くか | 効き目 | 危うさ | 直し方 |
|---|---|---|---|---|---|---|
| O1 | `Rendering/BoardRenderer.cs:297-301`（`Fill`）・`:224-225`・`:247` | 長方形 1 つごとに `new SolidColorBrush`（凍結しない）。スロットの前後・経験値の線・湯気・熱で **1 回の描き直しに 80〜120 個**。 | 盤は 140 ms ごとに変わる（下の O2）ので **毎秒 500〜800 個の Brush**。凍結していない Brush は描画の記録ごとに合成スレッド側の資源も作る。Gen0 GC の回数と合成の手間の両方に効く | **中〜大**（描画側の CPU と割り当ての大半） | 低 | `Color` → 凍結済み Brush の辞書（色の種類は有限: 固定色 10・湯気 12・熱 3×256）。または固定部（スロット・下地）を 1 回焼いた `DrawingGroup` にして貼る |
| O2 | `MainWindow.xaml.cs:55`・`:411-445`・`assets/atlas.json` | コマ送りは 140 ms 固定。`idle` のコマも 140 ms なので、**メモリくん（使用率 3% 超で `idle`）が居る限り、盤はほぼ毎回描き直しになる**（指紋で省けるのは全員 `sleep` のときだけ） | 「放っておくと 7 fps で描き続ける」常駐物になっている。広告の 1.8% の多くはここと推定 | **大** | 中（見た目の速さが変わる） | ①ロック中・画面オフ・セッション切断（`SystemEvents.SessionSwitch` / `PowerModeChanged`・`WM_POWERBROADCAST`）の間は `_frameTimer` を止める（計測と経験値は続ける）②窓が他の全画面の窓に完全に隠れている間も止める ③設定に「動きを控えめに」（コマ間隔 ×2） |
| O3 | `MainWindow.xaml.cs:416-442` | 140 ms ごとに `List<BoardSlot>`・record 8〜12 個・`Any` のクロージャ・`HashCode` | O1 に比べれば小さいが、描かない回（指紋が同じ回）にも毎回出る | 小 | 低 | 指紋を先に `_slots` から計算し、変わったときだけ `BoardSlot` を作る |
| O4 | `Sensors/PdhSampler.cs:110-160`（v0.7.3） | `GPU Engine(*)` のインスタンス（数百〜千超）ごとに Regex 2 回・部分文字列・`Convert`・鍵の文字列 | 計測（2 秒ごと）で一番多い割り当て。`RegexOptions.Compiled` は起動時に IL を組み立てる | 中 | 低 | **このブランチで実施**（`bc1fd31`。下の 6 章） |
| O5 | `Sensors/PdhSampler.cs:110-145`（v0.7.3） | 計測のたびに `Dictionary<string, Dictionary<string,double>>` を新しく作る・`ReadArray` が `IEnumerable`（状態機械）＋ `PtrToStructure` | 同上 | 小〜中 | 低 | 辞書を使い回して `Clear()`、`ReadArray` は `unsafe` で `PDH_FMT_COUNTERVALUE_ITEM*` を直接読む |
| O6 | `Sensors/SystemSampler.cs:79-116` | コア別の計測のたびに `AllocHGlobal`・配列 2 本・`PtrToStructure` を論理コア数ぶん | 128 コアでも数 KB だが毎回 | 小 | 低 | バッファと配列を使い回し、`MemoryMarshal.Cast` で読む |
| O7 | `Game/Composition.cs:47-113` | 計測ごとに PartySlot と補間文字列（Detail）を作り直す | 指していない限り Detail は使われない | 小 | 低 | Detail は吹き出し・数値欄を出すときだけ作る |
| O8 | `Core/Game/PartyState.cs:363-385` | 60 秒ごとに字下げ付き JSON をリフレクションで作り、`Flush(true)`（fsync） | 1 時間に 60 回の fsync。HDD の機械では起こし続ける | 小 | 中（落ちたときに失う分が増える） | 書く間隔を 5 分＋閉じるとき・最初からのときに。字下げを外す（読めるので互換は保たれる） |
| O9 | `Core/Game/Story.cs`・`AdventureBook.cs`・`PartyState.cs:350,366` | JSON はリフレクション。200 KB の story.json は場面ごとに `JsonDocument` を経て二度読む | 起動の時間と JIT・常駐のメモリ（初期の作業集合） | 中（起動） | 中 | `JsonSerializerContext`（源泉生成）。LenientJson と独自の変換器の試験を先に厚くする |

### 2.2 数値欄を開いているとき

| # | 場所 | 何が起きているか | 効き目 | 危うさ | 直し方 |
|---|---|---|---|---|---|
| O10 | `MainWindow.xaml.cs:799-865` | 0.5 秒ごとに数値欄を**丸ごと作り直す**（Grid・ColumnDefinition・TextBlock・ToolTip・Border・Brush。コア数ぶんの粒も）。毎回レイアウトをやり直す | 中（開いている間） | 低〜中 | 行を 1 回だけ作って値だけ差し替える（行の数が変わったときだけ作り直す） |
| O11 | `MainWindow.xaml.cs:873-876` | `LoadBrush` が呼ぶたびに新しい Brush（3 色しか無い） | 小 | 低 | 凍結済みの static 3 つ |
| O12 | `MainWindow.xaml.cs:359` と `PdhSampler.Sample` の注記「1秒ごとに1回だけ」 | 開いている間は 0.5 秒ごとに PDH を読む。GPU Engine の全走査が倍になる（注記とも食い違う） | 小〜中 | 低 | 展開中も 1 秒に。数値の見た目はほぼ変わらない |

### 2.3 起動・大きさ・AOT

- **単一 exe の自己完結・圧縮**（`PartsLife.csproj`）は、起動時に展開するぶん初回の作業集合が増える。README が既に説明している（展開してでも 1 本にする判断）。WPF は NativeAOT・トリミングに未対応なので、本体の AOT 化は現実的でない。
- `System.Text.RegularExpressions` は O4 の後、常駐の経路から読み込まれなくなる（点検の `BuildRules` だけ）。
- `TieredPGO`・ワークステーション GC・非同時 GC は常駐物として妥当。加えて `<TieredCompilationQuickJitForLoops>true</…>`（既定）と `ReadyToRun`（単一 exe でも可）は起動を縮めるが、exe が大きくなる。**測ってから**。
- `GCHeapAffinitizeMask` / `GCHeapHardLimit` は要らない。代わりに `<GCConserveMemory>`（0〜9）は常駐物の作業集合を下げる余地がある（実測で判断）。

---

## 3. 不具合・脆い所

本体（Linux で読んだ範囲）:

| # | 場所 | 内容 | 重さ | 直し方 |
|---|---|---|---|---|
| B1 | `MainWindow.xaml.cs:180-190`（`Guarded`） | 例外を数えるだけで、61 回目で**描画も計測も黙って止まる**。数は戻らないので、まれな例外でも長く常駐すれば溜まる。止まったことは画面に出ない | 中 | 続けての失敗だけ数え、成功したら 0 に戻す。止めたらお知らせに 1 行出す |
| B2 | `Core/Game/PartyState.cs:388` | 保存の `catch {}`。同じファイルの注記どおり「保存が止まっても気づけない」 | 中 | 失敗が続いたら一度だけ知らせる |
| B3 | `Sensors/DiskSampler.cs:48`・`:69-72` | 記憶装置の種類は起動時の WMI 1 回だけ。**後から繋いだ内蔵/USB の SSD は HDD 扱い**（番号が引けず `uint.MaxValue`）で、HDD くんが立つ | 小〜中 | 新しいドライブ文字が見えたら WMI を引き直す（1 分に 1 回まで、PdhSampler の Enumerate と同じ形） |
| B4 | `MainWindow.xaml.cs:339-345`（`IsOnScreen`）・`:400-406`（`KeepOnScreen`） | 仮想デスクトップの外接矩形で判定。**大きさの違うモニタを並べた L 字の隙間**（どのモニタにも無い所）に窓が残りうる | 小 | `MonitorFromPoint(MONITOR_DEFAULTTONULL)` で「どこかのモニタの作業領域に頭が入っているか」を見る |
| B5 | `MainWindow.xaml.cs:333-335`（`PlaceWindow`） | 初回の置き場所は `SystemParameters.WorkArea`（主モニタ・主モニタの DPI の DIP）。PerMonitorV2 で主と副の倍率が違うと、保存した `Left/Top`（DIP）が別のモニタで少しずれる | 小 | 位置は物理画素とモニタで保存（新しい欄の足し方は 5.6 と同じく後方互換） |
| B6 | `Sensors/PdhSampler.cs`／`GpuIdentity.cs:42` | LUID の上位を D3DKMT は `int`（符号付き）→`long`、PDH は 16 進を正の数で読む。上位の最上位ビットが立つ LUID だと鍵が食い違う（実際の LUID の上位はほぼ 0 なので理論上） | 極小 | 上位は 32 bit の符号なしに揃える（両側で） |
| B7 | `Startup.cs:31`・`:50` | `catch {}`。起動時の登録に失敗しても印だけ付いて見える（タブ版は作り直すので実際の状態に戻る） | 小 | 失敗したら一言出す |
| B8 | `App.xaml.cs:191-224`（`ShotCheck`） | `async void`（書き出し専用。中で全部 catch しているので実害なし） | 極小 | — |

点検・更新・Core（読み取りの係がもう一人で精読。主な物。**`src/PartsLife.Update` の物は意見だけで、変えるならオーナーの確認を**）:

| # | 場所 | 内容 | 重さ / 直す危うさ |
|---|---|---|---|
| B9 | `Checks/Protocol/HelperSession.cs:185-190` | 止めたときの 10 秒の打ち切りは、止めた時点で始まっていた読み取りには効かない（`line = await read` に期限が無い）。補助 exe が固まった I/O の中だと「止めています」のまま | 中 / 低（`read.WaitAsync(10s)`） |
| B10 | `Checks/Scan/SurfaceScanner.cs:117-120`・`Check/Program.cs:64-75` | 同期の `ReadFile`。弱った HDD は 1 ブロックに数十秒再試行し、その間 stop が効かない | 中 / 中（`CancelSynchronousIo`） |
| B11 | `Checks/SystemInfo/SystemCheck.cs:71-155`・`Checks/Build/BuildCollector.cs:81-174` | WMI に `EnumerationOptions.Timeout` が無い。壊れた記憶域スペースで「調べています…」のまま。`ManagementObject(Collection)` も Dispose していない | 中 / 低 |
| B12 | `Checks/SystemInfo/SystemCheck.cs:123` | `DriveInfo.GetDrives()` の全部に `IsReady`。切れたネットワークドライブで数十秒止まる | 中 / 低 |
| B13 | `Checks/SystemInfo/EventCounts.cs:22` | `0x100 /*TolerateQueryErrors*/` は誤り（0x100 は `EvtQueryForwardDirection`、TolerateQueryErrors は `0x1000`）。意図した寛容さが効いていない | 低 / 低（確認済み） |
| B14 | `Checks/Storage/Baseline.cs:497-520` | SMART の履歴が壊れていると黙って空にして次で上書き。書き込みに `Flush(true)` が無い（PartyState・UpdateStore はある） | 中 / 低（`.bad-日時` に退ける） |
| B15 | `CheckWindow/StoragePanel.cs:126` | `RecordAll`（ファイル I/O・SHA256・再試行の Sleep）を UI スレッドで同期 | 低〜中 / 低 |
| B16 | `CheckWindow/CheckWindow.cs:69-70` | 高さ 700 DIP 固定。1080p・150% の作業領域（約 688 DIP）からはみ出す | 低〜中 / 低 |
| B17 | `CheckWindow/CheckWindow.cs:115,131-141` | 閉じたあとの `GC.Collect`×2＋`WaitForPendingFinalizers` を UI スレッドで同期 → 常駐の窓が一瞬止まる。一度開いた点検のアセンブリは常駐が終わるまで残る（README の「常駐の負担 0」は「開くまで」） | 低 |
| B18 | `SystemPanel.cs:103`・`LoadPanel.cs:160`・`StoragePanel.cs:106,231`・`Updates/UpdateController.cs:117` | `CancellationTokenSource` を作り直すとき前の物を Dispose しない | 低 |
| B19 | `Updates/UpdateController.cs:203` → `UpdateApply.cs:61` → `Swapper.Apply` | 入れ替え（照合・写し・写しの照合）を UI スレッドで同期。窓は隠した後だが HDD では数秒 Dispatcher が止まる。**変えるならオーナー確認** | 低 |
| B20 | `Update/Updater.cs:122` | `CryptographicException`・`Win32Exception` が catch に無く、LastError に理由が残らない（外側の catch-all が拾うので落ちはしない）。**意見のみ** | 低 |
| B21 | `Update/SwapBoot.cs:96-105`・`Updates/UpdateApply.cs:115-132` | Mutex の Release が「await の後に UI スレッドへ戻る」ことに頼っている。将来 `ConfigureAwait(false)` にすると印が残る。**意見のみ（注記を足す程度に）** | 低 |
| B22 | `Update/UpdateHttp.cs:420` | 403 をすべて回数制限として 24 時間待つ。**設計判断。意見のみ** | 低 |

---

## 4. 合いそうな機能（合う度 × 手間の順）

どれも**送信しない**。通信は今までどおり更新の確認だけ（「確認しない」なら 0）。

| 順 | 案 | 合う度 | 手間 | 中身 |
|---|---|---|---|---|
| 1 | **ロック中・画面オフで動きを止める**（O2） | ◎ | 小 | 計測と経験値は続け、描くのだけ止める。常駐の負担を一番下げる |
| 2 | **SMART の推移を見せる** | ◎ | 小〜中 | `BaselineStore` は既に 1 台 200 件の履歴を持つ。寿命の消費 %・書き込み量・メディアエラー・通電時間の小さな折れ線と、1 日あたりの消費からの残り寿命の目安。新しく集める物は無い |
| 3 | **部品の入れ替わりの記録**（ハードウェアの履歴） | ◎ | 中 | 起動ごと（または点検ごと）に構成を `%APPDATA%\PartsLife\history.json` に残し、「前回から変わった部品」を出す。物語に「新しい仲間が来た」の一行（Director は顔ぶれの変化を既に知っている）。保存は別ファイルなので state.json の形は変わらない |
| 4 | **控えめな知らせ** | ○ | 小〜中 | 管理者なしで読める物（MSFT_PhysicalDisk の HealthStatus・WHEA の件数の増え・空き 10% 未満）を 1 日 1 回だけ見て、変わったら既存の `ShowNotice` に 1 行。「前回の精密チェックから 30 日」も。タブ「表示」に入/切 |
| 5 | **構成コードを QR で出す**（web-autoai.com のマイPC へ） | ○ | 小 | 通信せずにスマホで取り込める。コードの形・伏せ字の決まりは変えない（QR はコードの文字列をそのまま描くだけ）。QR の符号化は自前か小さな MIT のライブラリ |
| 6 | **点検の報告書をローカルに書き出す**（HTML / Markdown） | ○ | 小〜中 | 修理店・掲示板への相談用。シリアルは既存の MaskSerial で伏せる |
| 7 | **タブを隠す設定・タブの位置を覚える** | ○ | 小 | 5.6 のとおり、足すなら新しい欄として足す（古い版でも消えない） |
| 8 | 更新の知らせに変更点の要約 | △ | 小 | Release の JSON は既に取っている。追加の通信なし |
| 9 | NVIDIA の GPU 温度（nvml があるときだけ） | △ | 中 | README が予告している物。無い機械では読み込まない。常駐の負担と信用の両面で慎重に |

---

## 5. 窓の上のタブ（設計と試作）

### 5.1 ねらい

右クリックの献立は「右クリックしないと出ない」ので、在ることに気づかれにくい。とはいえ別の設定窓を作ると、これまでの方針（`MainWindow.xaml.cs:655-659` の「設定画面は作らない」）に反し、常駐の玩具に画面が 1 枚増える。
そこで、**同じ窓の上に細いタブを並べ、選んだときだけその下に設定の欄を開く**。何も選んでいない（「ようす」）間は今までの姿にタブの行が 1 本加わるだけ。

### 5.2 献立とタブの対応（`src/PartsLife.Core/Ui/MenuMap.cs`）

| 右クリックの献立（v0.7.3 の並び） | タブ | 欄での形 |
|---|---|---|
| 常に前面に表示 | 表示 | ○/● の切り替え（📌 と同じ処理） |
| Windows起動時に開く | 起動・更新 | ○/● |
| English / 日本語 | 表示 | 「言語」: 日本語 / English（いまどちらかが見える） |
| 名札 ▸ レベルだけ / 名前とレベル / 出さない | 表示 | 「名札」: 3 択 |
| 最初から ▸ 物語だけ / レベルだけ / 両方とも | データ | 押しボタン 3 つ。**押すと今までと同じ確認**（同じ `ConfirmReset` を通る） |
| 更新 ▸（いまの版・最後の確認・状態・今すぐ確認・3 つの設定・更新の内容） | 起動・更新 | 同じ中身（`UpdateController.Model()` を献立と共有）。「確認しない」の間は「今すぐ確認」を押せない |
| 点検… | 点検 | 説明＋「点検の窓を開く」 |
| 終了 | （タブに置かない） | 窓の ✕ と献立に残す（設定のつもりで押して閉じないように） |

献立もタブも**この 1 つの表から組み立て**、押したら同じ `Execute(MenuCommand)` を通る。表の決まり（全項目がちょうど 1 回・v0.7.3 の並び・取り返しのつかない物は「データ」と小献立だけ・終了はタブに出さない）は `MenuMapTests` が縛る。

### 5.3 形

```
┌──────────────────────────────────────────────────────┐
│ [ようす] [表示] [起動・更新] [点検] [データ]          │ ← タブ（いつも出ている。選択中は下線）
├──────────────────────────────────────────────────────┤
│ ● 常に前面に表示                                     │ ← 設定の欄（タブを選んだときだけ）
│ 言語    ● 日本語  ○ English                           │
│ 名札    ● レベルだけ  ○ 名前とレベル  ○ 出さない     │
├──────────────────────────────────────────────────────┤
│ 第二章 …  ·  湯けむりの沢           EN 📌 ⤢ ✕        │ ← 今までどおり（物語と小さなボタン）
│ 物語の 1 行目                                        │
│ 物語の 2 行目                                        │
├──────────────────────────────────────────────────────┤
│  [盤：CPU くん・FAN・メモリ・GPU …]                   │
├──────────────────────────────────────────────────────┤
│                         ▼                            │
└──────────────────────────────────────────────────────┘
「起動・更新」:  ● Windows起動時に開く
                 更新  いまの版 v0.7.3 · 最後の確認 …
                 ● 自動で更新する ○ 確認だけして知らせる ○ 確認しない
                 [今すぐ確認] [更新の内容（GitHub）]
「データ」:      最初から  取り消せません。押すと確認が出ます…
                 [物語だけ最初から] [レベルだけ最初から] [両方とも最初から]
```

### 5.4 常駐の窓・盤との共存

- 枠なし・透過・最前面・タスクバーに出ない窓のまま。タブの行と欄の幅は盤の枠（`MinPanelWidth` 520 以上）に揃える（`Redraw` で幅を入れる）。
- 欄を開くと窓が下に伸びる。伸びた後に `KeepOnScreen` を呼び、画面の下にはみ出さないようにする。
- 欄は**開いている間だけ**作る。畳んでいる間は何もしないので、常駐の負担は増えない（計測・描画の時計には触っていない）。
- **タブが開いている間は更新の入れ替えをしない**（`IsIdleForUpdate` に `_tab == None` を足した。献立・確認の窓と同じ扱い）。
- 窓の掴み（空いた所の左ドラッグ）・右下の掴み・右クリックの献立・EN/📌/⤢/✕ は今までどおり。
- `--shot` の宣材画像にはタブの行が 1 本加わる（撮り直しが要る。隠したければ `RenderShot` で `TabBar` を畳む 1 行）。

### 5.5 キーボード・読み上げ・DPI

- タブは `RadioButton`（読み上げで「n 個中 m 番目」）。タブの行は `TabNavigation=Once`＋矢印で巡回、Space で選ぶ。
- **Ctrl+1〜5** でタブ、**Esc** で畳む（窓に焦点があるとき）。タブの吹き出しに案内を出す。
- 欄の選択肢は `CheckBox` / `RadioButton`（○/● の見た目だけ差し替え。状態は読み上げに乗る）。キーボードの焦点が当たると枠が光る。
- 欄を作り直したあと、焦点を同じ番目の項目へ戻す（押した直後に焦点が消えないように）。
- 文字は拡大しない（今までの物語の文字と同じ扱い）。PerMonitorV2 のまま、WPF の DIP でそろうので倍率の違うモニタでも崩れない想定（**要実機確認**）。
- 残り: 読み上げソフトでの読み方（Narrator / NVDA）、ハイコントラストでの色（今は固定色。`SystemParameters.HighContrast` で枠と文字を系の色にする案）。

### 5.6 今までの人のために・保存

- **右クリックの献立は残す。** 並び・文言・確認は v0.7.3 と同じ（表から組み立てるが、表の並びを試験で固定）。
- **保存の形は変えていない。** タブの状態は保存しない（起動はいつも畳んだ姿）。設定はすべて今までの `PartyState` の欄（`Topmost`・`Lang`・`Labels`・`UpdateMode`）と HKCU の Run をそのまま読み書きする → 移行は不要。古い版に戻しても何も失わない。
- 今後「タブを隠す」などを足すなら、`PartyState` に新しい欄を足すだけでよい（無い保存は既定値、古い版は `[JsonExtensionData]` で残して書き戻す）。版の数（`SaveVersion`）を上げる必要もない。

### 5.7 試作のファイル

- `src/PartsLife.Core/Ui/MenuMap.cs` — 対応表（WPF なし）／ `tests/PartsLife.Core.Tests/MenuMapTests.cs`（12 件）
- `src/PartsLife/MainWindow.Tabs.cs` — タブ・欄・献立の組み立てと `Execute`（前面・言語・名札の処理の二重持ちも 1 つに）
- `src/PartsLife/MainWindow.xaml` — `TabBar`・`SettingsPanel` と 3 つの Style（`TabButton`・`OptionToggle`・`ActionButton`）
- `src/PartsLife/MainWindow.xaml.cs` — 古い `ShowMenu` を除き、上の物に繋ぐ
- `src/PartsLife/Updates/UpdateController.cs` — 更新の小献立の中身を `Model()` に寄せ、`CheckNow`・`OpenNotes`・`SetMode`・`Modes()` を出す（**確かめ・入れ替えの決まりは触っていない**）
- `src/PartsLife/I18n/Strings.cs` — タブの文言（日英）

---

## 6. このブランチで入れた物と、Windows で確かめること

### 6.1 コミット

| コミット | 中身 |
|---|---|
| `e59e15e` | 献立とタブの対応表（MenuMap）と試験 |
| `32d16cb` | 窓の上のタブ（試作）。献立も同じ表から。更新の小献立の中身を共有 |
| `bc1fd31` | 計測: GPU のカウンタ名を正規表現なしで読み、読んだ名前は覚える（`GpuCounterName`＋試験 22 件。v0.7.3 の正規表現と 2 万件の名前で突き合わせ） |
| （この文書） | `docs/cloud-analysis-20261011.md` |

変えていない物: 版の数・release.yml の署名・`src/PartsLife.Update` の照合の決まり・構成コードの形と伏せ字・保存の形。

### 6.2 Linux でのビルドと試験

- .NET SDK 10.0.112（Ubuntu の apt）。`dotnet build PartsLife.slnx`（`EnableWindowsTargeting`）: **成功・警告 0**（WPF の XAML の組み立ても含む）。
- `PartsLife.Core.Tests`: **113 件すべて成功**（v0.7.3 は 79 件。MenuMap 12・GpuCounterName 22 を追加）。
- `PartsLife.Update.Tests`: **142 件すべて成功**。
- `PartsLife.Checks.Tests`: ビルドは通るが、**Linux には Microsoft.WindowsDesktop.App の実行環境が無く回せない**（Windows で回すこと）。

### 6.3 Windows で確かめること

1. `dotnet test PartsLife.slnx`（特に Checks.Tests）と Release の publish が通る。
2. タブ: 5 つ並ぶ／押すと欄が開き、もう一度・「ようす」・Esc で畳む／Ctrl+1〜5／Tab と矢印・Space／欄を開いたとき窓が画面の下にはみ出さない（画面の下端近くに置いて）。
3. 各項目が献立と同じに効く: 前面（📌 の薄さも連動）・言語（タブの文言も切り替わる）・名札 3 択・起動時に開く（HKCU Run に書かれ、外れる）・更新の 3 設定（「確認しない」で「今すぐ確認」が押せず吹き出しが出る）・今すぐ確認の結果が欄に映る・更新の内容がブラウザで開く・点検の窓が開く・最初からの 3 つは確認が出て、取り消すと何も変わらない。
4. 右クリックの献立が v0.7.3 と同じ並び・文言・印で出る。
5. タブを開いたままにすると更新の入れ替えが起きない（試験用の行き先で）。閉じれば入れ替わる。
6. 125%・150%・倍率の違う 2 画面で、タブの行と欄の幅・字・枠が崩れない。ハイコントラストでの見え方。
7. Narrator でタブが「タブ n / 5」相当に、選択肢が選択状態つきで読まれる。
8. GPU（`bc1fd31`）: `--probe` の「画面に出る GPU の行」が v0.7.3 と同じ（複数 GPU の機械で）。`--bench sense` で前後の CPU と割り当て（dotnet-counters の alloc-rate）を比べる。
9. 広告の値（約 1.8% / 118 MB）を同じ条件で測り直す（タブの追加で増えていないこと）。
