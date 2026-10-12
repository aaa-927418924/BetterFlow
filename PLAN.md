# Auto Prompt 実装計画・記録

## 目的

入力欄にフォーカスしてショートカットを押し、話した内容をローカルで文字起こしし、OpenCodeの文章モデルで10行以下の簡潔なプロンプトに整えて元の入力欄へ貼り付ける。初回セットアップは画面上の一つの操作で日本語音声モデルを取得して使えるようにする。

## 決定事項

- 対象はWindows x64。配布物は.NET 8を同梱した自己完結型の単一EXE。
- セットアップ画面は添付参考画像に合わせた濃い角丸パネル、左上のアイコン、右下の明るい `Setup` ボタンで構成する。標準タイトルバーを使わない透過フレームレスウィンドウにし、パネルを画面いっぱいに広げる。上部をドラッグ領域、右上にカスタムの閉じるボタンを置く。`Setup` の文字は無効時も黒で表示する。
- 画面・ウィンドウ・タスクバー・EXEのアイコンはプロジェクト直下の `icon.svg` を元に生成する。
- 音声認識はNVIDIA `parakeet-tdt_ctc-0.6b-ja` のINT8 ONNXモデル。初回にGitHubからアーカイブを取得し、サイズとSHA-256を確認してユーザーのローカルアプリデータ領域へ展開する。
- 文章変換はOpenCode Zen Free `Muse Spark 1.3 Contributor`（provider ID `opencode`、model ID `muse-spark-1.3-contributor-free`）。
- Auto PromptはOpenCodeを同梱・ダウンロードせず、各PCに既にあるOpenCode CLIを `PATH` から検索する。PC名やユーザー名を含む絶対パスをコードへ固定しない。
- OpenCodeは利用者の通常のユーザー環境・設定・認証で起動する。Auto PromptはAPIキーを受け取らず、認証ファイルやOpenCode設定ファイルも読み書きしない。OpenCode Zenの接続はOpenCode側で済ませておく必要がある。
- OpenCodeのheadless serverはloopbackの空きポートで起動し、そのサーバープロセスだけに一時的なHTTP Basic認証を設定する。アプリ終了時に自分で起動したサーバーと空の一時作業フォルダーを閉じる。
- 変換要求ではモデルを明示し、ツールを無効化する。一時セッションは変換後に削除する。音声は端末外へ送らず、文字起こしをOpenCode Zenへ送信して生成結果を受信する。Muse Spark 1.3 Contributor Freeのプロンプト・回答がMetaの将来モデルの学習に使われる条件を、初回送信前の確認で明示する。[OpenCode Zen](https://docs.opencode.ai/docs/zen/)
- グローバルショートカットは `Ctrl + Shift + Space`。同じキーで録音を止め、ローカル文字起こしと文章変換を行い、元の入力欄へ貼り付ける。

## 音声認識モデル選定根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)の比較値を参照する。Whisper Large V3 TurboはCER 4.81%、Parakeet TDT-CTC 0.6B JAは5.94%。Parakeetは誤り率が1.13ポイント高い一方、Whisper Large V3 Turboより軽量な候補として選んだ。値は公開ベンチマークのNeMo版に対するもので、配布するINT8 ONNX版の精度を保証しない。このアプリ独自の精度比較は行わない。

## 初回セットアップ

1. インストール済みOpenCode CLIを発見し、ユーザーのOpenCode Zen接続と指定モデルの利用可否を確認する。
2. 日本語音声モデルをダウンロードし、サイズ・SHA-256を照合して展開する。
3. Parakeetをメモリへロードする。
4. OpenCode serverをloopbackで起動して準備完了にする。

モデルアーカイブは約489 MB。展開中の一時ファイルを含めて約1.2 GBの空き容量を必要とする。モデル本体と設定は `Environment.SpecialFolder.LocalApplicationData` を基点とするため、保存先は各PCのユーザー環境に合わせて決まる。

## 実装・確認状況

- 初回セットアップ、モデル取得、SHA-256検証、ローカル文字起こし、グローバルショートカット、OpenCode連携、貼り付け、単一EXE発行を実装。
- 初回チェックポイント: `097a715 docs: checkpoint local OpenCode and dark UI revision`。
- OpenCode Serverの利用方法・loopback・Basic認証と、モデル指定形式は2026-10-12に公式OpenCode資料を確認。
- `Setup` の文字はWPF共通のTextBlockスタイルに色を上書きされないよう、ボタンテンプレート内のTextBlockで黒を直接指定する。
- 2026-10-12: セットアップ中の「別のプロセスがファイルを使用中」エラーは、SHA-256検証用ストリームを閉じる前に同じダウンロードファイルを移動していたことが原因。検証ストリームを移動前に閉じるよう修正。
- 同時セットアップも防ぐため、モデル準備をプロセス間ロックで直列化し、各実行のダウンロード一時領域を分離。ファイル置換は共有違反時に短時間再試行し、エラー表示へ失敗工程を加えた。
- Release単一EXEの発行に成功し、`artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` を同じ更新版へ差し替えた。サイズは81,257,283 bytes、SHA-256は `D7DE8395F940D2F4D62FC6B4DBB421BCFC76DC0A79495BD09A9173843AE67A87`。
- ビルド成功は確認済み。アプリの実起動、セットアップ再実行、マイク入力、OpenCode Zenへの実リクエスト、視覚確認は未実施。
- このPCではOpenCodeのバージョンコマンドは確認できたが、既存プロファイルの読み出しは実施しない。サーバー起動、認証、モデル生成、マイク、画面の実起動は確認していない。
- 2026-10-13: セットアップ済みの起動では初回セットアップ文言を出さず、準備中の画面を表示する。×は非表示化してプロセスを維持し、トレイのダブルクリックで復元、右クリックの2項目で完全終了とWindows起動時の自動起動を操作する。
- 自動起動は現在のEXEパスを実行時に取得してユーザー単位のRunキーへ登録し、`--tray`で起動する。起動時に音声モデル未導入なら初回Setup画面を表示する。アプリの実起動、トレイ描画、ログオン後の自動起動、メニュー操作は未確認。
- タスクトレイ・自動起動変更後のRelease単一EXE発行成功。`artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` は同じ版で、サイズは81,259,257 bytes、SHA-256は `F488C71F31DAD44B5F1189792F143C8F9929285553DDFA2031D96888F3EB3408`。
