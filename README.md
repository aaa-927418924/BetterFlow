# Auto Prompt

Windows x64向けの音声入力アプリです。入力欄にカーソルを置いて **Ctrl + Shift + Space** を押して話し、もう一度押すと録音を停止します。音声をPC内の日本語モデルで文字起こしし、OpenCode ZenのMuse Spark 1.3 Contributor Freeで10行以下のプロンプトに整えて、元の入力欄へ貼り付けます。

初回画面は白い外周と標準タイトルバーのない角丸フレームレスウィンドウです。上端をドラッグして移動でき、右上のボタンで閉じられます。`Setup` ボタンの文字は黒で表示します。

## 配布と初回セットアップ

配布ファイルは [`dist/AutoPrompt.exe`](dist/AutoPrompt.exe) だけです。.NET 8ランタイムを含む自己完結型の単一EXEとして発行します。初回のSetupでParakeet日本語モデルをダウンロードします。約490 MBのアーカイブを取得し、展開時に約1.2 GBの空き容量が必要です。モデルは `%LOCALAPPDATA%\AutoPrompt` に保存します。

このアプリは各PCにインストール済みのOpenCodeを `PATH` から探して起動します。ユーザー名やPC固有の絶対パスは設定しません。OpenCodeは通常のユーザー設定・認証を引き継ぎ、アプリはAPIキーを入力・読み取り・保存しません。OpenCode Zenへ接続済みで、`opencode/muse-spark-1.3-contributor-free` が利用できる必要があります。OpenCodeが未インストール、または未接続の場合は、OpenCode側で導入・接続してからSetupを実行してください。

## データの扱い

- 音声の録音と文字起こしはPC内で行い、音声自体は送信しません。
- 文字起こしを接続済みのOpenCode Zenへ送信し、生成結果を受信します。音声は送信しません。
- Muse Spark 1.3 Contributor Freeでは、プロンプトと回答をMetaの将来モデルの学習に使う条件です。アプリは初回送信前に確認を表示します。[OpenCode Zenのモデル・データ利用条件](https://docs.opencode.ai/docs/zen/)
- OpenCodeへは一時セッションを作成し、変換後に削除します。サーバーはloopbackだけで待ち受け、ツール呼び出しを無効にして文章変換に使います。
- OpenCodeのユーザー設定・認証ファイルは読み書きせず、OpenCode自身が通常どおり参照します。

## 音声認識モデルの根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)で、Parakeet TDT-CTC 0.6B JAはCER 5.94%、Whisper Large V3 Turboは4.81%でした。Parakeetの方が1.13ポイント高い誤り率です。この比較は公開測定を選定根拠にしたもので、このアプリで独自の精度テストは行っていません。ベンチマーク測定はNeMo版の重みであり、アプリが取得するINT8 ONNX版の精度を保証しません。

## アイコン

ルートの `icon.svg` から `Assets/AutoPrompt.png` と複数サイズの `Assets/AutoPrompt.ico` を生成します。PNGは画面内、ICOはウィンドウ・タスクバー・EXEに使用します。再生成する場合は `pwsh -NoProfile -File tools/Generate-Icons.ps1` を実行してください。

## 開発状態

- 初回セットアップ、モデル取得、SHA-256検証、ローカル文字起こし、グローバルショートカット、OpenCode連携、貼り付け処理を実装しています。
- Release構成の単一EXEを発行します。
- この環境ではアプリの実起動、マイク入力、OpenCode Zenへの実リクエストを行っていません。

詳細と確認状況は [PLAN.md](PLAN.md) を参照してください。
