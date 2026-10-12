# Auto Prompt

Windows x64向けの音声入力アプリです。入力欄にカーソルを置いて **Ctrl + Shift + Space** を押して話し、もう一度押すと停止します。音声をPC内の日本語モデルで文字起こしし、OpenCode Zenで10行以下の短いプロンプトに整えて元の入力欄へ貼り付けます。

## 配布

配布ファイルは [`dist/AutoPrompt.exe`](dist/AutoPrompt.exe) だけです。Windows x64と.NET 8ランタイムを同梱した自己完結型の単一EXEとして発行しています。EXEのサイズは約81 MBです。

初回起動時に画面のセットアップボタンを押すと、OpenCodeと日本語音声モデルをダウンロードして起動します。初回取得にはインターネット接続と約1.2 GB以上の空き容量が必要です。音声モデルは約490 MBをダウンロードし、次回以降はユーザーの`%LOCALAPPDATA%\AutoPrompt`に保存したものを使用します。

Muse Spark 1.3 Contributor Freeを使うには、各利用者がOpenCode Zenのアカウント/APIキーを準備し、画面でキーを登録する必要があります。APIキーはWindows DPAPIでユーザー単位に保護して保存します。文字起こしテキストと生成結果はZenへ送信されます。Contributorモデルは入力と出力を将来のMetaモデル学習に使う条件があるため、登録前に画面の同意文を確認してください。無料提供や利用地域は変わる場合があります。

## 音声認識モデルの根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)で、Parakeet TDT-CTC 0.6B JAはCER 5.94%、Whisper Large V3 Turboは4.81%でした。Parakeetの方が1.13ポイント高い誤り率です。この比較は公開測定を選定根拠にしたもので、このアプリで独自の精度テストは行っていません。ベンチマーク測定はNeMo版の重みであり、アプリが取得するINT8 ONNX版の精度を保証しません。

## 開発状態

- 初回セットアップ、モデル取得、SHA-256検証、ローカル文字起こし、グローバルショートカット、OpenCode連携、貼り付け処理を実装しています。
- Release構成の単一EXE発行を確認しました。
- この環境ではアプリの実起動、マイク入力、Zenへの実リクエストを行っていません。

計画と実装の記録は [PLAN.md](PLAN.md) を参照してください。
