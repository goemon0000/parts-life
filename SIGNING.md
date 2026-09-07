# コード署名方針 / Code Signing Policy

このリポジトリで配布する実行ファイルの、作り方と署名の扱いです。
SignPath Foundation の[条件](https://signpath.org/terms.html)が求める公開文書を兼ねています。

## 何を配っているか

- `PartsLife.zip` … 中身は `PartsLife.exe` 1つ。**こちらを推奨**
- `PartsLife.exe` … 展開せずそのまま実行できる版
- 配布場所は **GitHub Releases のみ**です。ほかの配布元は当方と無関係です

2つの違いは**単一ファイルの自己圧縮の有無だけ**で、中身は同じソースから作った
同じアプリです。圧縮版は起動時に中身をまるごとメモリへ展開するため、
常駐中の占有が倍近くになります（実機で 222MB / 130MB）。

## どう作っているか

**GitHub Actions（`.github/workflows/release.yml`）が、このリポジトリのソースから
`windows-latest` 上でビルドします。** 手元の機械で作った実行ファイルは配りません。

各 Release には `SHA256.txt` を添えています。ダウンロードした物が
CI の作った物と同じかどうかは、これで確かめられます。

```powershell
Get-FileHash PartsLife.exe -Algorithm SHA256
```

## 署名について

| | |
|---|---|
| 証明書 | SignPath Foundation（申請中） |
| 種別 | OV |
| 署名する物 | 2つの `PartsLife.exe`（ZIP の中身と、直接実行できる版） |
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

- 通信しません
- 管理者権限を要求しません
- 書き込むのは `%APPDATA%\PartsLife\`（設定と物語の進行）と、
  自動起動を有効にした場合のみ
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` です
- アンインストールは exe を消すだけです。上の2箇所も消せば完全に元通りになります

## 依存している物

すべて .NET 10 の標準ライブラリと Windows の標準 API だけです。
第三者のバイナリを同梱していません（`System.Management` は Microsoft 公式パッケージ）。

## 連絡先

不具合・脆弱性の報告は [Issues](https://github.com/goemon0000/parts-life/issues) へ。
公開したくない内容は https://web-autoai.com/contact からお願いします。
