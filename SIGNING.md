# コード署名方針 / Code Signing Policy

このリポジトリで配布する実行ファイルの、作り方と署名の扱いです。
SignPath Foundation の[条件](https://signpath.org/terms.html)が求める公開文書を兼ねています。

## 何を配っているか

- `PartsLife.zip` … 中身は `PartsLife.exe` と、点検の補助 `PartsLife.Check.exe`（2MB）の2つ。**こちらを推奨**
- `PartsLife.exe` … 展開せずそのまま実行できる版
- 配布場所は **GitHub Releases のみ**です。ほかの配布元は当方と無関係です

2つの違いは**単一ファイルの自己圧縮の有無だけ**で、中身は同じソースから作った
同じアプリです。圧縮版は起動時に中身をまるごとメモリへ展開するため、
常駐中の占有が倍近くになります（実機で 222MB / 130MB）。

`PartsLife.Check.exe` は、点検の「記憶媒体の精密チェック」だけが使う管理者の補助 exe です
（押したときだけ UAC で許可を求め、読むだけで終わる）。ZIP にだけ同梱しているので、
単体の `PartsLife.exe` ではその面だけ使えません。

## どう作っているか

**GitHub Actions（`.github/workflows/release.yml`）が、このリポジトリのソースから
`windows-latest` 上でビルドします。** 手元の機械で作った実行ファイルは配りません。

各 Release には `SHA256.txt` を添えています。ダウンロードした物が
CI の作った物と同じかどうかは、これで確かめられます（アプリの自動更新も、これと照合してから入れ替えます）。

```powershell
Get-FileHash PartsLife.exe -Algorithm SHA256
```

## 署名について

| | |
|---|---|
| 証明書 | SignPath Foundation（申請中） |
| 種別 | OV |
| 署名する物 | 2つの `PartsLife.exe`（ZIP の中身と、直接実行できる版）と、ZIP の中の `PartsLife.Check.exe` |
| 承認 | Release ごとに維持者が手動で承認します |

### 署名しても最初は警告が出ます

**これは正直に書いておきます。** 2024年に Microsoft が仕様を変えて、
EV 証明書でも SmartScreen の警告を即座に消せなくなりました。
いまは EV も OV も同じ扱いで、**ファイルごとに評価が貯まるのを待つ**しかありません。

つまり署名は「警告を消す道具」ではありません。署名で得られるのは:

- 「不明な発行元」ではなく、発行元の名前が出るようになる
- 配布物が途中で書き換えられていないことを確かめられる
- 版を重ねるほど評価が貯まりやすくなる

警告が出た場合は「詳細情報」→「実行」で起動できます。
それが嫌な場合は、ソースを読んでご自分でビルドしてください。

## 中身について

- **通信するのは更新の確認とダウンロードだけ（GitHub）です。** 設定（右クリック →「更新」→「確認しない」）で止められます。
  ほかには何も送りません（下記「更新の確かめ方」）
- 管理者権限を要求しません
- 書き込むのは `%APPDATA%\PartsLife\`（設定と物語の進行）、`%LOCALAPPDATA%\PartsLife\updates\`（更新の覚え書きと、落とした新しい版）、
  更新を入れ替えるときの exe の置き場所（`PartsLife.exe.old` を作り、確かめが済んだら消す）と、
  自動起動を有効にした場合のみ
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` です
- アンインストールは exe を消すだけです。上の場所も消せば完全に元通りになります

## 更新の確かめ方

アプリは GitHub Releases から新しい版を落として、自分で入れ替えます（README の「更新」）。
**新しい鍵は作っていません。** 署名がまだ無いので、手で落とすときに慎重な人がやる確かめを、そのまま機械にやらせています。

| 確かめること | どうやって |
|---|---|
| 配布元 | `https://api.github.com/repos/goemon0000/parts-life/releases/latest` が返した Release の添付物だけ。URL がこの repo のそのタグの添付物の形でなければ使わない。下書き・試験版は使わない |
| 経路 | https だけ。行き先は `api.github.com`・`github.com`（この repo の添付物の場所だけ）・`release-assets.githubusercontent.com`・`objects.githubusercontent.com`（添付物の転送先）に限る。転送も 1 つずつこの表に照らす |
| 中身 | 同じ Release の `SHA256.txt` の行と、落とした物の SHA-256 が一致すること。GitHub が添付物ごとに記録している digest が**あれば**、それとも一致すること（無い Release では SHA256.txt だけで照合する）。ZIP の中身は ZIP 丸ごとの値で決まる（中の exe の行は無い。`SHA256.txt` の `PartsLife.exe` の行は単体で配っている exe の値で、ZIP の中の物とは別物） |
| 署名 | いま置いてある同じ名前の exe が署名つきなら、新しい exe も WinVerifyTrust で有効、かつ署名者の名乗り（Subject）が同じであること。SignPath Foundation の証明書は多くの OSS で共有なので、exe の版情報の製品名・元のファイル名（会社名があればそれも）も同じであること。**いまの exe の署名を確かめられない（壊れている・手元の証明書だけでは辿れない）ときは更新しない**（閉じる側に倒す）。確かめの間は通信しない（失効の確認を外し、手元の証明書だけで確かめる） |
| 版と大きさ | 新しい exe に書かれた版が Release のタグと同じで、いまより新しい（古い版に戻さない）。200MB まで、Release の記載どおりの大きさ。ZIP の中身は `PartsLife.exe` と `PartsLife.Check.exe` の 2 つちょうど |

**いまは署名の申請中で、配っている exe は無署名です。** そのため、いまの exe が無署名なら、無署名の更新も
（`SHA256.txt` との照合を通したうえで）入れています。署名つきの版が出たら、無署名の更新は断るように変えます
（`src/PartsLife.Update/Authenticode.cs` の `SignaturePolicy.RequireSignature`。署名つきの版に一度上がった人は、
その時点で「同じ名乗りの署名」が必須になるので、無署名の版には戻りません）。

`SHA256.txt` と添付物は同じ GitHub のアカウントから上がるので、この照合が守るのは「途中で壊れた・すり替えられた」までです。
repo の持ち主のアカウントが乗っ取られた場合は防げません（ブラウザで落とす人と同じ立場です）。それを防ぐのが署名です。

入れ替えた新しい版がうまく起動しなかったときは、元の版（`PartsLife.exe.old`）に戻し、その版は二度と入れません。

## 依存している物

すべて .NET 10 の標準ライブラリと Windows の標準 API だけです。
第三者のバイナリを同梱していません（`System.Management` は Microsoft 公式パッケージ）。

## 連絡先

不具合・脆弱性の報告は [Issues](https://github.com/goemon0000/parts-life/issues) へ。
公開したくない内容は https://web-autoai.com/contact からお願いします。
