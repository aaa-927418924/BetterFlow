# Auto Prompt

Windows x64向けの音声入力アプリです。入力欄にカーソルを置いて **右Alt** を押して話し、もう一度押すと録音を停止します。ショートカットとAIモードはセットアップ後の同じ画面から変更できます。既定ではAIを使わず、PC内で文字起こしした文章をそのまま入力欄へ貼り付けます。AIモードでは選択したサービスで文章を整え、話し方や敬語・くだけた表現を保ってフィラーや言い直しを調整します。初期設定はOpenCode Zen FreeのMuse Spark 1.3 Contributorです。設定画面で別のZen FreeモデルやDeepSeek、OpenRouter、Gemini、OpenAI互換APIへ変更できます。言い直しでは変更された内容だけを置き換え、計画など変わらない依頼も残します。箇条書きは発話で複数項目が明確に列挙されたときだけ使います。

初回画面は白い外周と標準タイトルバーのない角丸フレームレスウィンドウです。上端をドラッグして移動でき、右上のボタンで閉じられます。`Setup` ボタンの文字は黒で表示します。

## 配布と初回セットアップ

配布ファイルは [`dist/AutoPrompt.exe`](dist/AutoPrompt.exe) です。.NET 8ランタイムを含む自己完結型の単一EXEです。初回のSetupでParakeet日本語モデルをダウンロードします。約490 MBのアーカイブを取得し、展開時に約1.2 GBの空き容量が必要です。モデルは `%LOCALAPPDATA%\AutoPrompt` に保存します。

セットアップ後の画面では完了見出しと録音ボタンを表示せず、設定したショートカットで録音を開始・停止します。セットアップ済みなら、次回起動時はウィンドウを表示せずにタスクトレイへ常駐します。トレイアイコンをダブルクリックするとウィンドウを戻せます。右クリックメニューから完全終了とWindows起動時の自動起動を切り替えられます。音声モデルの読み込みが完了するまでショートカットは登録されません。自動起動はユーザー単位で登録し、起動時はトレイに常駐します。

録音・認識・文章整形中は、画面下中央に背景透過の小さなステータスインジケーターを表示します。表示全体を前のサイズの約半分に縮め、アイコンが切れないようにしました。完了・失敗の表示は処理終了から1秒後に消えます。フルHD・表示倍率100%では画面下端から約12pxの位置で、他のアプリの入力やクリックを妨げません。

OpenCode Zen Freeを選んだ場合、AIモードをオンにすると各PCにインストール済みのOpenCodeを `PATH` から探して起動し、選択された無料モデルを使います。ユーザー名やPC固有の絶対パスは設定しません。OpenCodeは通常のユーザー設定・認証を引き継ぎ、アプリはZen用APIキーを入力・読み取り・保存しません。別のAPIサービスを選ぶ場合は、サービス、モデルID、APIキーを設定します。OpenAI互換APIはHTTPSを使い、HTTPはlocalhostなどローカル接続に限ります。APIキーはWindowsのユーザー保護機能で暗号化して保存します。OpenCodeの準備ができない場合、AIモードへの切り替えは完了せず、文字起こしモードのまま使えます。

## データの扱い

- 音声の録音と文字起こしはPC内で行い、音声自体は送信しません。
- 既定の文字起こしモードでは音声・文字起こしともPC内だけで処理します。
- AIモードの場合だけ、文字起こしを選択したOpenCode ZenまたはAPIサービスへ送信し、生成結果を受信します。音声は送信しません。
- 初期設定の無料モデルでは、文字起こしと回答をMetaの将来モデルの学習に使う条件です。アプリは初回送信前に確認を表示します。[OpenCode Zenのモデル・データ利用条件](https://docs.opencode.ai/docs/zen/)
- OpenCode Zenでは一時セッションを作成し、変換後に削除します。サーバーはloopbackだけで待ち受けます。既定のPlanエージェントと「ツールを使わず文章だけを返す」指示で文章変換を依頼します。
- APIサービスは設定画面でモデルIDと必要なAPIキーを指定します。DeepSeekとOpenRouterはChat Completions API、Geminiは`generateContent` API、OpenAI互換は指定ベースURLのChat Completions APIを使います。[DeepSeek](https://api-docs.deepseek.com/api/create-chat-completion/)、[OpenRouter](https://openrouter.ai/docs/api/api-reference/chat/send-chat-completion-request)、[Gemini](https://ai.google.dev/gemini-api/docs/generate-content/text-generation)
- OpenCodeのユーザー設定・認証ファイルは読み書きせず、OpenCode自身が通常どおり参照します。

## 音声認識モデルの根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)で、Parakeet TDT-CTC 0.6B JAはCER 5.94%、Whisper Large V3 Turboは4.81%でした。Parakeetの方が1.13ポイント高い誤り率です。この比較は公開測定を選定根拠にしたもので、このアプリで独自の精度テストは行っていません。ベンチマーク測定はNeMo版の重みであり、アプリが取得するINT8 ONNX版の精度を保証しません。

## アイコン

ルートの `icon.svg` から `Assets/AutoPrompt.png` と複数サイズの `Assets/AutoPrompt.ico` を生成します。PNGは画面内、ICOはウィンドウ・タスクバー・EXEに使用します。再生成する場合は `pwsh -NoProfile -File tools/Generate-Icons.ps1` を実行してください。

## 開発状態

- 初回セットアップ、モデル取得、SHA-256検証、ローカル文字起こし、変更可能なグローバルショートカット、処理状況インジケーター、文字起こし／AIモード切替、話し方を保つAI整形、貼り付け処理を実装しています。変換結果の別カード表示は行いません。
- Release構成の単一EXEを発行します。
- この環境ではアプリの実起動、マイク入力、OpenCode Zenへの実リクエストを行っていません。

詳細と確認状況は [PLAN.md](PLAN.md) を参照してください。
