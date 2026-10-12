# Auto Prompt 実装計画・記録

## 目的

入力欄にフォーカスしてショートカットを押し、話した内容をローカルで文字起こしして元の入力欄へ貼り付ける。既定は文字起こしのみ。セットアップ後にAIモードを有効にすると、話者の言い方や語調を保ち、フィラーと自己訂正を整える。言い直しでは変更箇所だけを置き換え、計画など変わらない依頼も残す。箇条書きは発話で複数項目が明確に列挙されたときだけ使う。初回セットアップは画面上の一つの操作で日本語音声モデルを取得して使えるようにする。

## 決定事項

- 対象はWindows x64。配布物は.NET 8を同梱した自己完結型の単一EXE。
- セットアップ画面は添付参考画像に合わせた濃い角丸パネル、左上のアイコン、右下の明るい `Setup` ボタンで構成する。標準タイトルバーを使わない透過フレームレスウィンドウにし、パネルを画面いっぱいに広げる。上部をドラッグ領域、右上にカスタムの閉じるボタンを置く。`Setup` の文字は無効時も黒で表示する。
- 画面・ウィンドウ・タスクバー・EXEのアイコンはプロジェクト直下の `icon.svg` を元に生成する。
- 音声認識はNVIDIA `parakeet-tdt_ctc-0.6b-ja` のINT8 ONNXモデル。初回にGitHubからアーカイブを取得し、サイズとSHA-256を確認してユーザーのローカルアプリデータ領域へ展開する。
- 既定の文章変換サービスはOpenCode Zen Freeで、既定モデルは `Muse Spark 1.3 Contributor`（provider ID `opencode`、model ID `muse-spark-1.3-contributor-free`）。セットアップ後にOpenCode Zen Freeの利用可能な無料モデル、DeepSeek、OpenRouter、Gemini、またはOpenAI互換APIとモデルIDを選べる。
- 既定はAIを使わない文字起こしモード。セットアップ後のショートカット設定画面にあるトグルでAIモードを切り替え、選択をユーザー設定へ保存する。OpenCodeはAIモードを選ぶ時だけ起動し、文字起こしモードではOpenCodeへ接続・送信しない。
- AIモードの通常整形は、話者の言語・話し方・敬語/くだけた表現を維持し、意味のないフィラー・重複を除去、自己訂正は最後の意図を採用する。文体を敬語に統一せず、要約・補足・推測もしない。
- 初回SetupではOpenCode公式GitHub Releases APIから最新安定版Windows x64 baseline CLIを取得し、公式APIのSHA-256とサイズを照合してから `%LOCALAPPDATA%\AutoPrompt\opencode` に展開する。既存OpenCodeが `PATH` にある場合はそちらを優先する。アプリはPATHやシステム設定を変更せず、ユーザー名・PC固有の絶対パスをコードへ固定しない。
- OpenCode Zen Freeは利用者の通常のユーザー環境・設定・認証で起動する。Auto PromptはZen用APIキーを受け取らず、認証ファイルやOpenCode設定ファイルも読み書きしない。OpenCode本体の自動導入はできるが、OpenCode Zenを初めて使うPCでは利用者によるサインイン・認証が必要。
- DeepSeek、OpenRouter、Gemini、OpenAI互換APIは直接接続する。APIキーはWindows DPAPIのユーザー保護で暗号化して設定へ保存し、平文では書き込まない。OpenAI互換APIはHTTPSを必須とし、HTTPはloopback接続に限る。APIキーはログへ出さない。
- OpenCodeのheadless serverはAIモードを使う時だけloopbackの空きポートで起動し、そのサーバープロセスだけに一時的なHTTP Basic認証を設定する。モードを文字起こしのみに戻す時とアプリ終了時に、自分で起動したサーバーと空の一時作業フォルダーを閉じる。
- OpenCode Zenの変換要求では選択モデルを指定し、無料モデル以外は拒否する。既定のPlanエージェントへ文章のみを返す指示を渡す。個別リクエストから全ツールを無効化する設定は送らない。OpenCode Zenの無料枠は、Read等を含まないツール一覧を拒否する既知の挙動があるため、Planの標準制限を使う。一時セッションは変換後に削除する。音声は端末外へ送らず、文字起こしを選択中のサービスへ送信して生成結果を受信する。初期設定モデルのデータ利用条件は初回送信前の確認で明示する。[OpenCode Zen](https://docs.opencode.ai/docs/zen/)
- 直接APIはDeepSeekとOpenRouterのChat Completions、Geminiの`generateContent`、OpenAI互換Chat Completionsを使う。ユーザーがモデルIDを設定する。APIキーの暗号化は現在のWindowsユーザーに結び付く。
- グローバルショートカットの既定値は右Alt。セットアップ完了後の画面で右Alt単独または修飾キーとの組み合わせへ変更でき、設定は再起動後も保持する。録音ボタンやセットアップ完了見出しは表示せず、右Alt単独は低レベルキーボードフックで検知する。
- 録音・認識・整形中は画像参照に合わせた背景透過の状態ピルを画面下中央に表示し、画面下端から約12px（フルHD・倍率100%）の位置に置く。クリックとフォーカスを受け取らない。

## 音声認識モデル選定根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)の比較値を参照する。Whisper Large V3 TurboはCER 4.81%、Parakeet TDT-CTC 0.6B JAは5.94%。Parakeetは誤り率が1.13ポイント高い一方、Whisper Large V3 Turboより軽量な候補として選んだ。値は公開ベンチマークのNeMo版に対するもので、配布するINT8 ONNX版の精度を保証しない。このアプリ独自の精度比較は行わない。

## 初回セットアップ

1. 日本語音声モデルをダウンロードし、サイズ・SHA-256を照合して展開する。
2. 既存OpenCode CLIがPATHに見つからない場合、公式GitHub Releases APIから最新安定版をダウンロードし、サイズ・SHA-256を確認してアプリ用領域に展開する。ダウンロードに失敗してもローカル文字起こしは利用でき、AIモード選択時に再試行する。
3. Parakeetをメモリへロードし、文字起こしのみで利用可能にする。OpenCode serverの起動・モデルへの接続はAIモード選択時に行う。

モデルアーカイブは約489 MB。展開中の一時ファイルを含めて約1.2 GBの空き容量を必要とする。OpenCodeは最新安定版のbaseline版を追加取得する。音声モデル・OpenCode CLI・設定は `Environment.SpecialFolder.LocalApplicationData` を基点とするため、保存先は各PCのユーザー環境に合わせて決まる。

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
- 2026-10-13: セットアップ済みの起動では初回セットアップ文言を出さず、準備中の画面を表示する方式から、起動直後からウィンドウを表示せずバックグラウンド初期化してトレイへ常駐する方式へ変更。トレイからウィンドウを復元できる。
- 自動起動は現在のEXEパスを実行時に取得してユーザー単位のRunキーへ登録し、`--tray`で起動する。起動時に音声モデル未導入なら初回Setup画面を表示する。アプリの実起動、トレイ描画、ログオン後の自動起動、メニュー操作は未確認。
- タスクトレイ・自動起動変更後のRelease単一EXE発行成功。`artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` は同じ版で、サイズは81,259,257 bytes、SHA-256は `F488C71F31DAD44B5F1189792F143C8F9929285553DDFA2031D96888F3EB3408`。
- 2026-10-13: 録音中・認識中・整形中・完了時の背景透過インジケーターを追加。セットアップ完了画面からグローバルショートカットを変更・保存でき、既定値を右Altへ変更。右Alt単独はRegisterHotKeyを先に試し、登録できない場合は低レベルキーボードフックへ切り替える。ほかの修飾キー組み合わせはRegisterHotKeyで登録する。
- 2026-10-13: `dotnet publish AutoPrompt.csproj -c Release -o artifacts/publish --no-restore` 成功。`artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` は同一、81,264,354 bytes、SHA-256 `9DA8565D53CEF11E58040A9E0E081B7BEB8062FEF2E5BE859E3C46C81DE5413B`。NuGet脆弱性フィードはネットワークに接続できずNU1900警告。アプリ起動、画面表示、クリック透過、右Alt検知、ショートカット再起動後保持は未確認。
- 2026-10-13: 状態ピルを元の幅・高さの約半分（140×38 DIP）へ縮小し、枠線と余白内にアイコンを収めた。完了・失敗の表示を処理終了から1秒で自動的に隠す。
- 2026-10-13: 右Alt単独ではRegisterHotKeyを使わず、低レベルキーボードフックを直接登録するよう修正。Windows上での実キー検知は未確認。
- 2026-10-13: `dotnet publish AutoPrompt.csproj -c Release -o artifacts/publish --no-restore` 成功。`artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` のSHA-256は一致、サイズは81,264,348 bytes、SHA-256 `9F303697B9FA29E0DDEF2427ADAA2FE44D8F425A9DB1153DCAE5767CEB02425B`。NuGet脆弱性フィードに接続できずNU1900警告。実画面の表示・寸法、クリック透過、右Alt検知、ショートカット再起動後保持は未確認。
- 2026-10-13: セットアップ済みかつ音声モデルファイルがある状態では、アプリ起動時にメインウィンドウを表示せず、音声認識モデルだけをバックグラウンドで初期化する。既定の文字起こしモードではOpenCodeを起動・接続しない。トレイアイコンからウィンドウを復元でき、音声認識モデルの読み込みに成功した後だけ設定済みショートカットを登録する。AIモードは設定画面で有効にした時にOpenCodeを起動する。
- 2026-10-13: 文字起こし後のOpenCode応答が空になる問題を調査。OpenCode 1.18.34がこの環境のPATH上で見つかった（バージョン表示のみ確認し、OpenCode設定・認証は読んでいない）。アプリの要求はOpenCodeの全ツールを無効化していた。OpenCode Zenの無料モデルでRead等を含まないツール一覧が拒否される既知の報告と一致するため、この上書きを削除し、既定Planエージェントを使うよう変更した。OpenCodeのレスポンスに含まれる`info.error`も表示するようにして、無料枠や上流エラーを「空の回答」に置き換えない。公開の[OpenCode issue #51315](https://github.com/anomalyco/opencode/issues/51315)でツール定義が不完全な要求をZen Freeが拒否する挙動を確認。一方、OpenCode 1.18.27でMuse Spark 1.3のカタログ/通信方式メタデータ欠落が報告された[issue #47120](https://github.com/anomalyco/opencode/issues/47120)もあり、1.18.34で同じ問題が残るかは未確認。モデルへの実リクエストはプライバシーのため実施せず、この環境での生成成功は未確認。
- 同日、Release単一EXEの発行と`dist/AutoPrompt.exe`の更新に成功。両ファイルは81,264,914 bytes、SHA-256 `111958221F084DE238E19425739D588CB909261A904BD668AC33F504C3AA8C05`。ビルド中にNuGet脆弱性フィードへ接続できずNU1900警告。アプリの実起動・トレイ操作・ショートカット・OpenCode Zenの生成は未確認。
- 2026-10-13: AIモード切替でOpenCode起動確認がタイムアウトする問題を調査。OpenCode 1.18.34を一時loopbackサーバーとして起動し、`/global/health`と`/provider`の確認は合計約4.5秒で成功、Zen接続とMuse Sparkモデル登録を確認した。モデル生成要求は送っていない。OpenCode公式資料に従いHTTP Basicユーザー名を明示し、loopback通信でシステムプロキシを無効化。起動確認時間を90秒へ延長し、health接続とZen/モデル確認を区別したタイムアウト表示に変更。セットアップ後は完了見出し・録音ボタンを隠し、ショートカット操作を維持する。アプリ内トグルの実操作は未確認。
- 同日、Release単一EXE発行と`dist/AutoPrompt.exe`差し替えに成功。発行元と配布先は81,266,904 bytes、SHA-256 `EB41095D05528D278DE441A69C8BE638D4A50686EBAC77EB8947B926C8BA5325`で一致。NU1900が発生。画面・トグル・ホットキー・マイク・OpenCode Zenへの生成要求は未確認。
- 2026-10-13: 実装前チェックポイントを`3ece302 fix: stabilize OpenCode setup and transcription controls`として作成。
- 2026-10-13: 話し方維持の整形とプロンプトモードを追加したが、ユーザーによる実使用で箇条書きが不要な箇所にも出る問題が報告され、プロンプトモードは撤去。自己訂正では変更された内容だけを置き換え、計画など継続する依頼を保持する指示を追加し、通常は箇条書きを使わない。結果カードと「直前の変換結果」見出し、コピー操作も削除。Release単一EXE `artifacts/publish/AutoPrompt.exe` と `dist/AutoPrompt.exe` は81,266,346 bytes、SHA-256 `E87C097048F868159545E5F7E85AB3AB0A1BF84106E7BEE7FEBD71EA37019035` で一致。NU1900警告あり。新しい整形指示のMuse Spark実生成と画面実操作は未確認。
- 2026-10-13: OpenCode自動導入の実装前チェックポイントを`c6a73df feat: add configurable AI providers`として作成。
- 2026-10-13: 初回Setupで、PATH上に既存OpenCodeがない場合にOpenCode公式GitHub Releases APIの最新安定版Windows x64 baseline CLIをダウンロードし、APIが返すサイズとSHA-256を検証して`%LOCALAPPDATA%\\AutoPrompt\\opencode`へ展開する処理を追加。既存OpenCodeを優先し、PATHやシステム設定は変更しない。初回Setupでの取得が失敗してもローカル文字起こしは利用可能で、AIモード有効化時に再試行する。最新APIのv1.19.0 baseline ZIP（62,173,954 bytes、SHA-256 `2759ca3449fe946d82da3be43d2e85332127399686aafceaff153b90c3079c56`）はメタデータまで確認し、アーカイブ自体の取得・展開は未実施。公式Zen資料はアカウントへのサインインと認証情報の設定を要求するため、初回認証まで自動代行はせず、初回画面に案内を追加。
- 同日、配布資産名をWindows x64 baseline版へ統一して再発行し、`dist/AutoPrompt.exe`を更新。発行元と配布先は81,286,388 bytes、SHA-256 `37940B52F13C2A923EDE1747056ABFCE94B120E59138EF5ACAFCD99BDCEDA406`で一致。NuGet脆弱性フィード取得失敗によるNU1900警告あり。アプリの実起動、OpenCode自動ダウンロード・展開、Zen認証、モデルへの実要求、画面操作は未確認。テストスイートは実行していない。
