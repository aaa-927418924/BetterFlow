# Better Flow 実装計画・記録

## 目的

入力欄にフォーカスしてショートカットを押し、話した内容をローカルで文字起こしして元の入力欄へ貼り付ける。編集可能な入力欄で文章が選択されている場合は音声指示で文章を編集し、同じセッション中に続けて編集できる。通常テキストが選択されている場合はその文章について質問し、原文を変更せずに回答を表示する。新規設定ではAIモードをオンにし、Muse Spark 1.3 Contributor Freeで話者の言い方や語調を保ち、フィラーと自己訂正を整える。モデル選択は設定へ保存し、再起動後も維持する。言い直しでは変更箇所だけを置き換え、計画など変わらない依頼も残す。箇条書きは発話で複数項目が明確に列挙されたときだけ使う。初回セットアップは画面上の一つの操作で日本語音声モデルを取得して使えるようにする。

## 決定事項

- 対象はWindows x64。配布物は.NET 8を同梱した自己完結型の単一EXE。
- セットアップ画面は添付参考画像に合わせた濃い角丸パネル、左上のアイコン、右下の明るい `Setup` ボタンで構成する。標準タイトルバーを使わない透過フレームレスウィンドウにし、パネルを画面いっぱいに広げる。上部をドラッグ領域、右上にカスタムの閉じるボタンを置く。`Setup` の文字は無効時も黒で表示する。
- 画面・ウィンドウ・タスクバー・EXEのアイコンはプロジェクト直下の `icon.svg` を元に生成する。
- 音声認識はNVIDIA `parakeet-tdt_ctc-0.6b-ja` のINT8 ONNXモデル。初回にGitHubからアーカイブを取得し、サイズとSHA-256を確認してユーザーのローカルアプリデータ領域へ展開する。
- 文章変換はOpenCode経由に限定する。新規設定の既定モデルは `DeepSeek V4.1 Flash Free`（provider ID `opencode`、model ID `deepseek-v4.1-flash-free`）。設定画面の一覧はOpenCode serverのprovider/model一覧から取得し、OpenCode Desktopでプロファイルへ追加したモデルも表示・選択できる。選択したprovider/modelは設定ファイルへ保存し、再起動後も維持する。Better Flow内に直接API接続、APIキー、Base URLの設定を置かない。
- モデル一覧はOpenCode serverの`/provider`応答に含まれる`connected` provider IDで`all`カタログを絞り、接続済みproviderのモデルだけ表示する。`connected`情報が取れない場合は全カタログへのフォールバックをせず、更新をエラーにする。
- 新規設定ではAIモードをオンにする。セットアップ後のショートカット設定画面にあるトグルでAIモードを切り替え、選択をユーザー設定へ保存する。既存設定に保存済みのオン/オフは尊重する。OpenCodeはAIモードを使う時だけ起動し、文字起こしモードではOpenCodeへ接続・送信しない。
- AIモードの通常整形は、話者の言語・話し方・敬語/くだけた表現を維持し、意味のないフィラー・重複を除去、自己訂正は最後の意図を採用する。文体を敬語に統一せず、要約・補足・推測もしない。
- 初回SetupではOpenCode公式GitHub Releases APIから最新安定版Windows x64 baseline CLIを取得し、公式APIのSHA-256とサイズを照合してから `%LOCALAPPDATA%\Better Flow\opencode` に展開する。既存OpenCodeが `PATH` にある場合はそちらを優先する。アプリはPATHやシステム設定を変更せず、ユーザー名・PC固有の絶対パスをコードへ固定しない。旧AutoPrompt保存先がある場合は設定とモデルを保持したまま新しい保存先へ移行する。
- OpenCodeは同じWindowsユーザーの通常のDesktop/CLIプロファイルと認証を使う。OpenCode Desktopで追加・接続したprovider/modelは一覧更新後にBetter Flowから選べる。Better FlowはOpenCode設定ファイルと認証ファイルを直接読み書きせず、モデル一覧と生成をOpenCode server経由で扱う。
- 音声入力デバイスは設定画面で選択可能にする。初期値はWindowsの既定マイク（WaveIn mapper）で、個別デバイスの指定も保存する。
- OpenCodeのheadless serverはAIモードを使う時だけloopbackの空きポートで起動し、そのサーバープロセスだけに一時的なHTTP Basic認証を設定する。モードを文字起こしのみに戻す時とアプリ終了時に、自分で起動したサーバーと空の一時作業フォルダーを閉じる。
- OpenCodeの変換要求では一覧から選んだprovider IDとmodel IDを指定し、既定のPlanエージェントへ文章のみを返す指示を渡す。個別リクエストから全ツールを無効化する設定は送らない。OpenCode Zenの無料枠は、Read等を含まないツール一覧を拒否する既知の挙動があるため、Planの標準制限を使う。一時セッションは変換後に削除する。音声は端末外へ送らず、AIモード時だけ文字起こし、または選択文と音声指示を選択モデルへ送信する。OpenCode Desktopで選択した接続先の利用規約とデータ利用条件が適用され、アプリ独自の初回送信確認は表示しない。[OpenCode Zen](https://docs.opencode.ai/docs/zen/)
- 選択文編集はショートカットで連続録音を開始し、Silero VADで発話区間を検出してから文字起こし・OpenCode編集・安全な選択範囲の置換を行う。16 kHzの512サンプル窓、speech threshold 0.60、8窓（約256 ms）連続判定、512 ms pre-rollを使い、短い雑音による開始を抑える。Sileroモデルは単一EXEに同梱し、起動時にSHA-256を検証してローカルアプリデータへ展開する。処理後は新しい文を再選択し、UI Automationの更新遅延には最大300 msの再試行で対応し、同じ入力先で追加の音声指示を受け付ける。前面ウィンドウ、対象UI Automation provider、選択範囲が一致しない場合は貼り付けを中止する。
- グローバルショートカットの既定値は右Alt。セットアップ完了後の画面で右Alt単独または修飾キーとの組み合わせへ変更でき、設定は再起動後も保持する。録音ボタンやセットアップ完了見出しは表示せず、右Alt単独は低レベルキーボードフックで検知する。
- 録音・認識・整形中は画像参照に合わせた背景透過の状態ピルを画面下中央に表示し、画面下端から約12px（フルHD・倍率100%）の位置に置く。クリックとフォーカスを受け取らない。

## 選択した通常テキストへの質問機能（2026-10-14）

- 編集可能な入力欄で選択されている文章は、既存のOpenCode連続編集を維持する。通常テキスト（Webページや回答文など）が選択されている場合は、選択文を参考コンテキストとして音声質問を受け付け、原文の置換・貼り付けを行わない。
- 回答方式を設定へ追加する。既定はOpenCodeのテキスト回答で、文字起こしした質問と選択文を保存済みのprovider/modelへ送る。AIモードのオン/オフとは独立し、ユーザーが選択して起動した質問のみ送信する。OpenCode SSEのテキスト差分を回答ウィンドウへ逐次表示する。
- 任意でGPT Live音声会話を選べる。ChatGPTサブスクリプションOAuthとWebRTCを用い、選択文を参考コンテキストとして渡し、回答音声と同じ会話の回答文字列を表示する。会話はショートカットで開始・停止する。OAuth情報はDPAPI保護、WebView2 Runtimeが不足する場合は署名を検証して導入する。入力欄の文章編集はOpenCodeのまま維持する。
- 回答ウィンドウは下部インジケーターの上に表示し、スクロール可能なテキストと設定画面共通のスクロールバーを使う。角丸はインジケーターと同じ19px。テキスト回答とGPT Live回答の両方をストリーミング表示する。
- 実装状態: UI Automationの選択範囲を編集可能入力と通常テキストへ分類し、設定の回答方式を保存。OpenCode回答は選択文を未信頼資料として扱う専用プロンプトとSSE逐次更新を追加。GPT LiveはOAuth/WebView2/WebRTCセッションを任意モードとして再導入し、会話音声・発話テキスト・回答文字列の表示、同じショートカットでの終了を接続した。Release buildは警告0・エラー0。単体EXE発行とdist差し替えに成功し、`artifacts/publish-selected-text-qa/BetterFlow.exe` と `dist/Better Flow.exe` は82,400,260 bytes、SHA-256 `F6FAB1C2A4173736282F56EF0DBA41B36D77B7AF3953C41306748C627C42B032`で一致。更新前EXEは`artifacts/BetterFlow-before-selected-text-qa-20261014.exe`へ保存。実UI、OpenCode生成、ChatGPT OAuth/WebRTC、マイク、音声再生は未確認。テストスイートは実行していない。
- 質問セッションの録音方針: OpenCode質問モードは編集モードと同じSilero VADで無音区間を確定し、回答後も録音を続けて次の質問を受け付ける。前の質問・回答も次の質問の文脈へ渡す。GPT Liveモードではこれまで通りローカルVADを使わず、GPT Live側の音声ターン検出を維持する。
- 回答UIと音声イベント: 回答パネル上部の「回答」見出しとステータス行を外し、音声レベルに反応しない静止ベクターアイコンを置く。最初の空でないassistant出力が届くまで初回パネルを表示しない。OpenCodeのストリームはassistant message IDに紐づくtext partだけを表示し、質問・選択文などの送信内容を出さない。GPT Liveは途中の文字起こしイベントを確定回答にせず、completed/turn.doneの発話単位で一度だけ会話へ追加する。
- 実施結果: 変更前に`cb66094 feat: add selected-text question modes`をチェックポイントとして作成。OpenCode質問のSilero VAD連続処理、選択文と会話履歴を含む回答、応答開始までのパネル遅延表示、assistant message IDによる出力限定、GPT Liveの途中文字起こし抑制と重複抑制を実装。GPT LiveはWebRTC側の既存発話ターン処理を維持し、Silero VADを追加していない。Release build/publish成功（0エラー、NuGet脆弱性フィード取得不可のNU1900警告1件）。`artifacts/publish-selected-text-question-flow-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,401,843 bytes、SHA-256 `3E93C5612856FF51602A936F9674668E2554D8B72DA3B0953E45641F15206C8F`で一致。更新前EXEは`artifacts/BetterFlow-before-question-flow-20261014.exe`へ保存。テストスイートと実UI・マイク・OpenCode/GPT Live実会話は未確認。

## 音声認識モデル選定根拠

公開[FLEURS日本語ベンチマーク](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)の比較値を参照する。Whisper Large V3 TurboはCER 4.81%、Parakeet TDT-CTC 0.6B JAは5.94%。Parakeetは誤り率が1.13ポイント高い一方、Whisper Large V3 Turboより軽量な候補として選んだ。値は公開ベンチマークのNeMo版に対するもので、配布するINT8 ONNX版の精度を保証しない。このアプリ独自の精度比較は行わない。

## 初回セットアップ

1. 日本語音声モデルをダウンロードし、サイズ・SHA-256を照合して展開する。
2. 既存OpenCode CLIがPATHに見つからない場合、公式GitHub Releases APIから最新安定版をダウンロードし、サイズ・SHA-256を確認してアプリ用領域に展開する。ダウンロードに失敗してもローカル文字起こしは利用でき、AIモード選択時に再試行する。
3. Parakeetをメモリへロードし、文字起こしのみで利用可能にする。OpenCode serverの起動・モデルへの接続はAIモード選択時に行う。

モデルアーカイブは約489 MB。展開中の一時ファイルを含めて約1.2 GBの空き容量を必要とする。OpenCodeは最新安定版のbaseline版を追加取得する。音声モデル・OpenCode CLI・設定は `Environment.SpecialFolder.LocalApplicationData` を基点とするため、保存先は各PCのユーザー環境に合わせて決まる。

## AI整形を速くする計画（提案・未実装）

ユーザー報告では、Muse Spark 1.3の整形精度は高い一方、通常約5秒、長い場合は約15秒かかる。アプリ内の段階別時間はまだ測っていないため、モデルや指示文を先に変えず、まず待ち時間の所在を分ける。

1. `StopAndProcessAsync` と `OpenCodeService.RewriteAsync` に計時を追加し、文字起こし、OpenCode serverの起動/health/provider確認、選択モデル確認、session作成、モデル応答待ち、session削除、貼り付けまでを個別に記録する。通常利用のコールド起動とウォーム起動を分け、入力は文字数帯だけ記録する。音声・文字起こし・生成文・認証値は記録しない。
2. コールド起動が支配的なら、保存設定がAIモードオンの時に限り、音声モデルの初期化後にOpenCode serverをバックグラウンドで先行起動する。モデルへの要求は送らず、UIとショートカットを待たせず、アプリ終了までserverを再利用する。アプリ起動時にserverを起動しない現状は`MainWindow.xaml.cs`、各変換でモデル一覧確認・新規session作成・完了待ち・session削除を行う現状は`Services/OpenCodeService.cs`で確認した。
3. モデル確認やsession操作が計測上目立つ場合だけ、成功済みの接続モデル一覧をアプリ実行中にキャッシュし、明示的な一覧更新・モデル変更・失敗時に更新する。発話ごとのsession再利用は過去の文字起こしが次の入力に混ざる恐れがあるため行わず、独立sessionを保つ。
4. provider応答が主な待ち時間なら、Muse Sparkの選択を維持したままローカル側でできる改善には限界がある。OpenCode公式のモデル一覧ではMuse Spark FreeはZenのResponses endpointを使う。OpenCodeのモデル設定はprovider/modelごとに対応オプションが異なるため、Muse Sparkに公開されていない推論強度等の指定を推測で加えない。速さを優先する選択肢が必要なら、既定モデルは変えず、設定済みの高速モデルをユーザーが手動選択できる形にする。
5. 指示文の短縮は計測で入力処理が原因と分かった場合のみ候補にする。現在の文体維持、自己訂正、依頼条件の保持、不要な箇条書き防止を残し、短縮案は合成した短い例文で意味の落ち方を確認する。
6. SSE/非同期APIは生成を早くする手段ではなく、対応している場合に途中経過を早く見せる手段として別扱いにする。OpenCode公式Server docsは同期message応答と`prompt_async`/SSEを公開しているが、最近のOpenCode版でテキストdeltaが出ない報告もあるため、実装する場合は対象バージョンのイベントを先に確認し、完了前の部分回答を入力欄へ貼り付けない。

評価ではコールド/ウォーム別の中央値と90パーセンタイル、失敗率、文章の意味保持を比較し、精度を落とす設定変更は採用しない。出典: [OpenCode Server docs](https://dev.opencode.ai/docs/server/)、[OpenCode Models docs](https://docs.opencode.ai/docs/models/)、[OpenCode Zen model list](https://dev.opencode.ai/docs/ja/zen/)、[OpenCode issue #50285](https://github.com/anomalyco/opencode/issues/50285)。

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
- 2026-10-13: OpenCode自動導入の作業チェックポイントを`d21bd8c feat: install OpenCode during setup`として作成。
- 2026-10-13: 文章変換をOpenCode経由だけに変更し、Desktop/CLIプロファイルからOpenCode serverが返す全provider/modelを選択するUIへ一本化。Desktopで追加したモデルはOpenCode側で設定・接続後に一覧更新して選ぶ。直接API送信、APIキー/Base URL入力と保存、旧APIクライアントを削除し、既存Auto Prompt設定に保存された暗号化APIキーは新形式の設定保存時に破棄する。旧Muse Spark選択は引き継ぎ、旧API選択は一覧に同じprovider/modelがあれば引き継ぐ。それ以外はAIモードをオフにする。設定画面スクロールバーを細い丸角のカスタムThumbに変更し、モデル一覧の選択項目は白背景・黒文字にする。
- 2026-10-13: 設定画面に音声入力デバイス一覧を追加し、初期値はWindowsのシステム既定入力にする。選択を保存し、入力デバイスの再読み込みと切断時の既定マイクへのフォールバックを用意する。
- 2026-10-13: 初回Setupで、PATH上に既存OpenCodeがない場合にOpenCode公式GitHub Releases APIの最新安定版Windows x64 baseline CLIをダウンロードし、APIが返すサイズとSHA-256を検証して`%LOCALAPPDATA%\\AutoPrompt\\opencode`へ展開する処理を追加。既存OpenCodeを優先し、PATHやシステム設定は変更しない。初回Setupでの取得が失敗してもローカル文字起こしは利用可能で、AIモード有効化時に再試行する。最新APIのv1.19.0 baseline ZIP（62,173,954 bytes、SHA-256 `2759ca3449fe946d82da3be43d2e85332127399686aafceaff153b90c3079c56`）はメタデータまで確認し、アーカイブ自体の取得・展開は未実施。公式Zen資料はアカウントへのサインインと認証情報の設定を要求するため、初回認証まで自動代行はせず、初回画面に案内を追加。
- 同日、配布資産名をWindows x64 baseline版へ統一して再発行し、`dist/AutoPrompt.exe`を更新。発行元と配布先は81,286,388 bytes、SHA-256 `37940B52F13C2A923EDE1747056ABFCE94B120E59138EF5ACAFCD99BDCEDA406`で一致。NuGet脆弱性フィード取得失敗によるNU1900警告あり。アプリの実起動、OpenCode自動ダウンロード・展開、Zen認証、モデルへの実要求、画面操作は未確認。テストスイートは実行していない。
- 2026-10-13: OpenCodeのprovider/model一覧だけをAIモデル選択元にし、Desktopで設定したモデルを含む一覧更新UIに統一。直接API接続とAPIキー/Base URL UIを削除。設定画面にコンパクトなカスタムスクロールバー、選択項目の黒文字、マイク選択を追加。マイク初期値はWindowsのシステム既定で、選択デバイスが使えない場合は既定マイクへフォールバックする。Release単一EXEは81,281,071 bytes、SHA-256 `6DC8448B3004F61F5A9400A0824AF630C6A561FEFFDD99D65467DF4E49FED0D6`で発行し、`dist/AutoPrompt.exe`との差分なし。ビルド成功、NU1900警告あり。画面の実表示、マイク実入力、OpenCode/Desktopモデルへの実要求は未確認。テストスイートは実行していない。
- 2026-10-13: 画面でドロップダウン選択項目が明るい背景・白文字になって見づらい問題を、ComboBoxItemの明示テンプレートで明るい背景・黒文字に修正。OpenCodeモデル一覧は`/provider`の全カタログではなく`connected` provider IDに限定し、全providerを表示して8497件になる問題を修正。仕様は[OpenCode Server API](https://dev.opencode.ai/docs/server/)で確認。Release単一EXEは81,281,415 bytes、SHA-256 `C42E34368585735D23A13600213F7D26FC1EDACE71668F249D063CC225989C01`で発行し、`dist/AutoPrompt.exe`と一致。ビルド成功、NU1900警告あり。実画面での色確認とOpenCode接続一覧の実取得は未確認。テストスイートは実行していない。
- 2026-10-13: 新規設定のAIモードをオン、既定モデルをOpenCode Zen FreeのMuse Spark 1.3 Contributor Freeに変更。設定ファイルがない場合やモデルIDが空の場合は既定を保存し、選択したモデルは既存の選択イベントで再起動後も保持する。既存設定に保存済みのモードとモデルは維持する。Releaseビルド・単一EXE発行に成功し、発行元と`dist/AutoPrompt.exe`は81,283,162 bytes、SHA-256 `F83032AEDC1D5EA294C4E8DF359CD40D5F851FFF777FD49BFA44420AFDAA0DDC`で一致。NuGet脆弱性フィード接続失敗のNU1900警告あり。アプリ実起動とOpenCodeモデルへの生成要求は未確認。テストスイートは実行していない。
- 2026-10-13: 実装前チェックポイント`0c84b66 chore: checkpoint before voice selection editing`を作成。その後、選択中の文章をUI Automation TextPatternで取得し、音声指示をローカルで文字起こししてOpenCodeの選択モデルへ送り、対象アプリ・入力欄・選択文を再検証して置き換える連続編集機能を実装。音声は単語間の約850ms無音で区切り、各編集後に置換文を再選択して、再度ショートカットを押さずに追加指示を受ける。異なる前面アプリや選択範囲の変化を検出した場合は安全のためセッションを終了する。
- 同日、製品名・ウィンドウ名・トレイ名・OpenCode一時セッション名・アイコン資産・保存先をBetter Flowへ変更。既存`%LOCALAPPDATA%\AutoPrompt`は移動可能なら新しい`%LOCALAPPDATA%\Better Flow`へ同一ボリューム上で移動し、旧アプリが使用中なら旧保存先を使ってデータを保持する。Windows起動時登録も新しい値名へ移行する。
- `dotnet publish AutoPrompt.csproj -c Release -o artifacts/publish-betterflow-final --no-restore`成功。内部名前空間もBetterFlowへ変更した最終単一EXEを`artifacts/publish-betterflow-final/Better Flow.exe`と`dist/Better Flow.exe`へ発行し、両者は81,292,262 bytes、SHA-256 `DEFDAF24EF6F01CA340FE6C5F34816D2A30D4B9E23AFE3A4CB73079338ADF609`で一致。`dist`には新EXEだけを置いた。NuGet脆弱性フィード取得失敗のNU1900警告あり。アプリ実起動、マイク入力、UI Automationが使える個別アプリでの置換、OpenCodeモデルへの実生成は未確認。
- 2026-10-13: 選択文編集で短い雑音だけでもAI処理が始まる問題を修正。連続録音のRMSしきい値を廃止し、Sherpa-ONNX内蔵APIからSilero VADを利用する。16 kHz・512サンプル窓、0.60 threshold、連続8窓（約256 ms）の発話判定を開始条件とし、約512 msの先行音声を保持。Silero VADモデルはSherpa-ONNXが公開する互換ONNX（643,854 bytes、SHA-256 `9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6`）で、単一EXEへ埋め込み、初回使用時に検証してアプリデータへ展開する。AI編集後はUI Automationで挿入文の再選択を最大300 ms再試行し、連続編集を続けられるようにした。
- 2026-10-13: AI編集後に置換文の選択が実際の入力欄へ反映されない問題を修正。UI Automationが挿入範囲を特定し、キャレットを置換文末尾に配置したあと、SendInputでShift+左をテキスト要素数だけ送り、入力欄から選択文字列を読み返して確認する。挿入位置は貼り付け後のキャレットまたは元選択範囲の端点で照合し、重複する文章の誤選択を避ける。Releaseビルド後、実アプリでの選択ハイライトと次の音声指示による連続置換は未確認。
- `dotnet publish AutoPrompt.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish-betterflow-check --no-restore`成功。発行元と`dist/Better Flow.exe`は81,809,177 bytes、SHA-256 `7938500F38560B673969ABAD3EF7106655676891104BED8879CF02D1B97F1F21`で一致。`dist`には`Better Flow.exe`だけを配置。NuGet脆弱性フィードへ接続できずNU1900警告あり。実マイクでの雑音誤検出率、各対象アプリでの再選択、およびOpenCode生成は未確認。

## 固有名詞の文字起こし精度改善案（未実装）

2026-10-13調査。ユーザーの発話録音を収集・評価に使わず、公開ベンチマークを中心に改善案を選ぶ。

### 現状と制約

- `Services/AsrService.cs` はSherpa-ONNXの`NeMoCtc.Model`へモデルを読み込み、`greedy_search`で復号する。アプリ側から固有名詞や読みをデコーダーへ渡す語彙・hotword機能はない。
- 配布モデルはTDT/CTCハイブリッドの日本語Parakeetだが、Sherpa-ONNX配布スクリプトはCTCブランチだけをONNXへ書き出す。現在はそのCTC経路を使っている。
- Sherpa-ONNXのhotwords機能はtransducerモデル向けで、CTCモデルには直接使えない。NeMoにはCTC向けGPU Phrase Boostingがあるが、現在のCPU向けSherpa-ONNX経路へ設定を足すだけでは利用できない。
- 一般CERだけでは固有名詞の聞き取りを評価しきれない。既存のFLEURS比較（Parakeet 5.94%、Whisper large-v3-turbo 4.81%）は別の推論構成であり、Better FlowのINT8 CTC経路の精度を保証しない。

### 実施順

1. **公開評価の選定基準を固有名詞向けにする。** CERに加え、固有名詞F1または用語ヒット率、誤った候補を出す率を確認する。モデルID・推論経路・量子化・データセットが一致しない比較は直接の採用根拠にしない。公開結果が見つからない項目は未確認とする。
2. **ローカル用語辞書を追加する。** 設定から正式表記、読み、誤認識されやすい候補表記を登録・編集・削除できるようにする。AIモードがオフのときは明示登録された候補だけをローカルで置換する。自動学習や曖昧な一括置換は行わない。
3. **AIモードでは辞書候補を補正の文脈に使う。** 文字起こしと一致する可能性のある項目だけを抽出し、「発音と文脈が合う場合だけ正式表記を使う。確信がなければ勝手に補わない」と指示する。個人名などの辞書項目はOpenCodeへ送る追加情報になるため、送信は利用者が明示的に有効化した場合に限る。
4. **デコーダー／モデル変更は公開根拠が揃った場合に進める。** NeMoのCTC向け文脈バイアスや、同じ日本語Parakeetを基盤にしたJ-PACF-YOMI研究を調査対象にする。ただし同研究は調査時点で実験用の足場であり、配布可能な置換チェックポイントは公開されていない。Windows CPU実行、単一EXE配布、追加ダウンロード容量、ライセンスと、固有名詞評価の公開結果が確認できるまでは標準モデルを置き換えない。

### 根拠と注意点

- Sherpa-ONNXはhotwordsをtransducerモデルに限定していると説明し、NeMoはCTC向けGPU Phrase Boostingを別方式として説明している。
- 日本語固有名詞の公開評価ではCER順位と固有名詞F1順位が一致しない例が報告されている。ただし紹介されているParakeetは別モデル版で、評価は15発話の小規模IT領域データのため、現在の日本語Parakeetへ数値をそのまま当てはめない。
- 参考資料：[Sherpa-ONNX hotwords](https://k2-fsa.github.io/sherpa/onnx/hotwords/index.html)、[NeMo word boosting](https://docs.nvidia.com/nemo-framework/user-guide/latest/nemotoolkit/asr/asr_customization/word_boosting.html)、[日本語FLEURS比較](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark)、[J-PACF-YOMIのモデルカード](https://huggingface.co/saeeew/J-PACF-YOMI-tdt)、[JP-HomophoneBench](https://huggingface.co/datasets/saeeew/JP-HomophoneBench)、[固有名詞F1評価](https://neosophie.com/en/blog/20260421-asr-evaluation)。

## AI編集の1段階取り消し

- 2026-10-13: AI編集が文章を置換した後、画面下部の状態インジケーターの上に小さな「元に戻す」ボタンを3秒間表示する。クリックまたは編集中の音声指示（「元に戻して」「文章がバグっているから戻して」など）で、直前の置換だけを元の選択文へ戻す。
- 誤操作防止として、対象アプリのウィンドウ・UI Automation入力欄・現在選択中の編集結果を確認してから置換する。テキストが未選択・変更済み、または別アプリが前面の場合は復元しない。音声コマンドはOpenCodeへ送らずローカルで判定する。
- `dotnet build AutoPrompt.csproj -c Release --no-restore` とwin-x64自己完結型の単一EXE発行に成功。`artifacts/publish-undo/BetterFlow.exe` と `dist/Better Flow.exe` は81,814,089 bytes、SHA-256 `A88FDCB7152868887E6F9094DB170A69D4A00F2825816BE9FBE2F7AD45C4C74E`で一致。NuGet脆弱性フィードへ接続できずNU1900警告。Windows上のボタン表示・クリック、マイクからの取り消し発話、対象アプリごとの復元は未確認。

## 設定画面のコンパクト化

- 2026-10-13: 設定画面から指定された補足・完了テキストを削除。マイク名は専用テンプレートで省略せず折り返して表示する。ショートカット変更と音声/OpenCode一覧更新ボタンを共通の角丸スタイルへ揃え、AIモード中に無効なOpenCode更新ボタンはAIモードオフ時と同じ配色を維持する。
- Releaseビルドとwin-x64自己完結型の単一EXE発行に成功。発行元`artifacts/publish-settings-compact/BetterFlow.exe`と`dist/Better Flow.exe`は81,813,982 bytes、SHA-256 `3855AFE38AAE615C85F0AB8AC612C01530158E78216ED5016ADBFF06DA47F2DC`で一致。NuGet脆弱性フィードに接続できずNU1900警告。実画面での表示・クリック確認は未実施。

## 選択文のGPT Live音声編集（旧機能・削除済み）

- 以下は過去の計画・実装履歴。2026-10-14にユーザー判断でGPT Live機能を削除し、現在のアプリはOpenCodeのみを使用する。
- GPT LiveではChatGPTサブスクリプションのOAuth認証とWebRTCの双方向音声を使用する。Platform APIキー、API課金経路へのフォールバックは設けない。
- 選択文を会話の初期文脈として渡し、モデルが文章変更を求められた時だけアプリ内の限定ツールで現在の選択範囲を置換する。文法確認など変更を求めない発話には音声で返答し、文章は変更しない。置換後は選択範囲を保持し、同じライブセッションで連続編集を続ける。
- OAuthトークンはWindowsユーザーのDPAPIで保護し、WebView側へ渡さない。OpenCode方式は従来どおりローカルOpenCode serverを使う。設定画面には注意書き・規約案内の文章を追加しない。
- 実装順: WebView2/WebRTC実行基盤、ChatGPT OAuthのサインイン・更新・保管、会話ツールイベントと選択範囲置換、設定UIとモード永続化、README/配布記録の更新。認証クライアントまたはGPT Live側の契約変更で接続できない場合は、OpenCode既定動作を維持し、エラーを明示する。
- 2026-10-14にOpenClawのGPT Live実装とOpenAIのWebRTC資料を参照。GPT Liveのsubscription OAuthルートはOpenClawが利用するWebRTC接続経路を基にする。WebView2 RuntimeはWindows 11には含まれるが、一部のWindows 10環境にはないため、配布では不足時の導入を扱う。
- 実装: 編集エンジン設定を追加し、初期値と新規設定の既定をOpenCodeにした。GPT Liveは設定で選んだ時だけ有効で、ブラウザーOAuth、DPAPI保護のトークン保存・更新、WebView2 WebRTC音声、発話指示の委譲処理、OpenCodeモデルによる編集/質問判定、選択文の置換・再選択、音声応答を接続した。設定画面に利用上の注意書きは加えていない。WebView2 Runtimeがない場合はMicrosoftの署名を検証してからセットアップする。
- `dotnet build AutoPrompt.csproj -c Release --no-restore` と`dotnet publish AutoPrompt.csproj -c Release -o artifacts/publish-gpt-live --no-restore` 成功。`artifacts/publish-gpt-live/BetterFlow.exe` と`dist/Better Flow.exe`は82,344,329 bytes、SHA-256 `8EE4A29043BAB179DA90735253E26CCFC6DB22276C35F0436F594AE16BA90C40`で一致し、`dist`には単体EXEだけを配置。NuGet脆弱性フィードへ接続できずNU1900警告。アプリ起動、ChatGPT OAuth、GPT Live WebRTC、マイク選択、OpenCodeとの会話/編集は未確認。テストスイートは実行していない。
- 2026-10-14の利用報告の原因調査で、ChatGPT OAuth用`gpt-live-1-codex`に公開Realtime API側の声`marin`を指定していたことを確認し、Codexルートの既定声`cove`へ修正。GPT Liveの委譲イベントが文字起こしより先に届く場合、従来は最大1.6秒で空入力扱いにしていたため、最初の文字起こしを待ってから900msの無音区間で確定するよう変更。音声チャネル準備・GPT Live返答の状態を表示し、文字起こしが届かない場合もエラーを表示する。
- Releaseビルドとwin-x64自己完結型単一EXE発行に成功。`artifacts/publish-gpt-live-fix/BetterFlow.exe`と`dist/Better Flow.exe`は82,344,413 bytes、SHA-256 `98DB0CE4A59686A2A98C45F5555B08C81EC6F64B2F9635F7E96A43F53F0DC3FE`で一致。NU1900警告はNuGet脆弱性フィードへ接続できなかったため。GPT Liveの実ログイン・マイク・音声応答と選択文編集の実動作は未検証。
- 2026-10-14の追加障害調査で、OAuthサブスクリプション用`gpt-live-1-codex`が送る`input_transcript.added`・`turn.done`・`delegation.created`をアプリが処理せず、接続開始イベントだけを認識していたため、発話後の編集処理が開始されないと判明。また、返信も公開API用`session.*.append`形式で送っていた。OpenClawの[受信イベント処理](https://github.com/openclaw/openclaw/blob/main/extensions/openai/realtime-quicksilver-events.ts)と[メッセージ形式](https://github.com/openclaw/openclaw/blob/main/extensions/openai/realtime-quicksilver-protocol.ts)に合わせ、サブスク用イベント受信、重複し得る文字起こしの確定、`delegation.context.append`返信を実装。データチャネルが開くまで会話開始と表示しないようにし、文字起こし待ち・返信失敗も画面に表示する。
- 受信イベント修正後、WebRTC開始待機中に接続エラーまたはチャネル終了が届いた場合、60秒待たずに失敗を表示するよう変更。
- Releaseビルド・win-x64単一EXE発行・`dist/Better Flow.exe`差し替えに成功。`artifacts/publish-gpt-live-fix2/BetterFlow.exe`と配布先は82,345,476 bytes、SHA-256 `C984B789FF1DFE8244B9CF86703D42C39B665CB2ABD4C1647E89B075ED6D7079`で一致。差し替え前のEXEを`artifacts/BetterFlow-before-gpt-live-route-fix.exe`および`artifacts/BetterFlow-before-gpt-live-startup-fix.exe`へ保存。NU1900警告はNuGet脆弱性フィードへ接続できなかったため。ビルドは成功したが、実アカウント・マイク・GPT Liveの音声応答・選択文編集は未確認。テストスイートは実行していない。
- 2026-10-14: GPT Liveの初期発話・編集判定・応答の長さ・待ち状態表示を修正。選択文はJSONエスケープしてセッション指示内の文脈として渡し、起動時は新しい発話を待つ。明示的な編集指示を質問扱いした場合は専用の置換処理で再試行し、編集完了時は「完了」など1語、質問応答は1～2語に制限。インジケーターにGPT Live起動中・会話可能・認識・編集中・応答中を表示。Release buildと単体EXE発行に成功し、artifacts/publish-gpt-live-fix3/BetterFlow.exeとdist/Better Flow.exeは82,346,435 bytes、SHA-256 7537E4FDD028D20A32D0B15B59261E8C5F0E78D78F4AE950FEECC386584EC11Bで一致。配布先への更新を完了し、更新前版はartifacts/BetterFlow-before-gpt-live-edit-fix3.exeへ保存。NuGet脆弱性フィードに接続できずNU1900警告あり。ChatGPT OAuth・実マイク・GPT Live会話の実動作は未確認。テストスイートは実行していない。

- 2026-10-14: GPT Liveの応答言語と発話単位の対応付けを修正。セッション指示とOpenCodeの編集プロンプトで、指定翻訳言語はreplacementTextだけに適用し、音声応答は常に日本語と明示。編集完了時の余分なcommentaryをやめ、クライアントから返したspeakable文だけを読み上げる。文字起こしを共有バッファからFIFOの発話スナップショットへ変更し、input_transcript.added/turn.doneを同じ発話に統合、別発話は順番に処理。委譲IDの重複も抑止。Release build成功（NU1900はNuGet脆弱性フィード取得不可の警告）。単体EXEをartifacts/publish-gpt-live-fix4/BetterFlow.exeとdist/Better Flow.exeへ発行・反映し、両者は82,347,052 bytes、SHA-256 8E3DCA07D2A7909DF0518DE78F2120C7CB15761C1DC6530279CDC413CAF13018で一致。旧配布版をartifacts/BetterFlow-before-transcript-correlation-adjustment.exeへ保存。GPT Liveの実OAuth・マイク・文章編集は未確認。テストスイートは実行していない。

## GPT Liveの削除とマイク音量インジケーター（2026-10-14）

- ユーザー判断によりGPT Liveを機能ごと無効化。設定のエンジン選択・ChatGPT OAuth・WebRTCセッション・専用WebView2セットアップ・HTML資産と依存を削除。選択文編集は常にOpenCodeの連続音声編集経路を使う。旧settings.jsonにGPT Liveが保存されている場合も起動時にOpenCodeへ補正して保存する。
- 下部インジケーターのアイコンをSVGの形状に合わせた3本線へ置き換え、現在選択中のマイクから受け取るPCM16のRMSレベルに応じて線の高さを変える。26px幅表示時のSVG元寸法を上限にし、左13.0px、中央14.6px、右9.2pxを超えない。録音終了時は元のロゴ寸法へ戻す。
- Release buildは警告0・エラー0、win-x64自己完結型単一EXE発行に成功。`artifacts/publish-opencode-only/BetterFlow.exe`と`dist/Better Flow.exe`は81,818,617 bytes、SHA-256 `A09312A24D5F97FD9510D4F90B4811D2EB83912323E34AF53B3E7AEAE17CF022`で一致。WebView2へのパッケージ参照は削除済み。置換前EXEは`artifacts/BetterFlow-before-opencode-only.exe`および直前配布版`artifacts/BetterFlow-before-icon-color-adjustment.exe`へ保管。テストスイートは未実施。実マイクの波形表示とOpenCode生成・選択文編集は未確認。
- `dotnet restore AutoPrompt.csproj --ignore-failed-sources -p:NuGetAudit=false`、Release build、publishを実行。build成功・警告0・エラー0。テストスイートは未実施。Windows上のマイクレベル表示、OpenCode生成、選択文編集など実動作は未確認。
- 2026-10-14: 設定画面からモデルの「保存済みの選択」およびマイクの「選択中」表示を削除。AI設定内の「選択文の編集」セクションと説明文も削除し、モデル・マイクの保存と選択動作は維持。Release buildとwin-x64単体EXE発行に成功。`artifacts/publish-settings-copy-cleanup/BetterFlow.exe`と`dist/Better Flow.exe`は81,818,444 bytes、SHA-256 `259EEEC5D3476E1132DF2F6206390CC51570C998254504BCE5F10B155078D97A`で一致。差し替え前EXEを`artifacts/BetterFlow-before-settings-copy-cleanup.exe`へ保管。設定画面の実表示は未確認。テストスイートは未実施。

## 音声処理キャンセルと設定画面の省スペース化（2026-10-14）

- 通常の文字起こし/AI整形中にショートカットを再度押すと、処理用CancellationTokenをキャンセルし、貼り付け前にもキャンセルを確認する。選択文編集の処理中はセッションをキャンセルし、連続録音を止め、未処理の発話を破棄する。OpenCode起動/生成へキャンセルトークンを伝える。
- 設定ウィンドウ下部のRuntimeStatusログ表示を折りたたみ、セットアップ進捗表示は残す。設定画面の実表示と実処理中のキャンセル操作は未確認。
- Release buildは警告0・エラー0、win-x64単体EXE発行に成功。`artifacts/publish-cancel-and-compact-settings/BetterFlow.exe`と`dist/Better Flow.exe`は81,819,309 bytes、SHA-256 `9F644027E6939557FD8078719B9BA35F62420C38DFB819BD3D5651284D844972`で一致。差し替え前EXEを`artifacts/BetterFlow-before-cancel-and-compact-settings.exe`へ保管。テストスイートは未実施。

## 選択文編集モード中のショートカット即時キャンセル（2026-10-14）

- 編集セッションが有効な間は、次のショートカットを「録音停止して未処理発話を処理する」経路に渡さず、即座にセッションキャンセルへ分岐する。キャンセルトークンで処理中のASR/OpenCode編集を止め、連続マイクを停止し、待機中の発話を破棄する。
- Release buildは警告0・エラー0、win-x64単体EXE発行と`dist/Better Flow.exe`の差し替えに成功。`artifacts/publish-edit-session-hotkey-cancel/BetterFlow.exe`と配布先は81,819,276 bytes、SHA-256 `605A9C1DDE7D8EDFD425FF64A9640F06C056B7A05B80214A971FCEE46BB1C474`で一致。差し替え前EXEは`artifacts/BetterFlow-before-edit-session-hotkey-cancel.exe`へ保存。テストスイートは未実施で、実アプリ上のショートカットキャンセル動作は未確認。

## ホットキー開始時の効果音（2026-10-14）

- `Assets/universfield-gadget-activation-250072.mp3`を単一EXE内のリソースとして再生する。素材は`.gitignore`対象で、リポジトリには追加しない。
- 通常録音と選択文編集の両方で、マイク開始が成功した直後にだけ再生する。停止・キャンセル・VAD処理開始などでは呼び出さない。再生失敗は録音処理へ影響させない。
- MP3更新後にRelease build/publishを再実行し、`dist/Better Flow.exe`を更新。発行物と配布先は81,863,898 bytes、SHA-256 `F55BBC8E086C6ABA1E02BEA48B9BFB56C361F58F4FCE9944953C147D0A2E3D71`で一致。差し替え前は`artifacts/BetterFlow-before-activation-sound-v2.exe`へ保存。素材は48,960 bytes、SHA-256 `5E90FDE4E056A9FE396559591C1B8DF2DB1B0BCAF68F5B85B37F92599A769264`で、Gitignore対象。音声デバイス上での実再生は未確認。ビルド警告0・エラー0。テストスイートは実行していない。
## OpenCode経由のAI整形の実測と対策（2026-10-14）

- 計測条件: OpenCode 1.18.34の一時loopback serverと現在のOpenCode profile/authを使用。アプリと同じ`agent=build`、接続中のprovider/model、同期`POST /session/:id/message`、要求ごとのsession作成・削除で、同じ短い合成文を各モデルへ2回送信。system指示は実アプリの文面に沿った短い計測用版で、完全なUI経由再現ではない。各provider/modelの初回要求をcold、直後の要求をwarmと呼ぶ。出力はいずれも31文字。AI整形部分のAPI計測で、マイク/VAD/文字起こし/UI貼り付けは含まない。各条件1回ずつで統計的保証はない。

| モデル | 初回生成 | 直後の生成 | session作成 | session削除 |
|---|---:|---:|---:|---:|
| GPT 6 Luna Fast (`openai/gpt-6-luna-fast`) | 3.94秒 | 2.77秒 | 19 / 7 ms | 30 / 11 ms |
| Muse Spark 1.3 Contributor Free (`opencode/muse-spark-1.3-contributor-free`) | 43.46秒 | 2.93秒 | 30 / 13 ms | 9 / 8 ms |
| Gemini 3.5 Flash Lite (`google/gemini-3.5-flash-lite`) | 75.30秒 | 44.07秒 | 14 / 26 ms | 15 / 7 ms |

- OpenCode serverのhealth readyは1.06秒、接続モデル一覧取得は2.74秒。session操作は7–30 msで、測定した数秒〜75秒の主因ではない。待ち時間のほぼ全てが同期message要求の内側にあった。
- 判断: Museは初回だけ約40.5秒長く、直後は約2.9秒まで戻った。provider/model cold startまたは初回経路待ちが有力候補だが、first-token時刻未計測のため確定できない。Geminiは直後も約44秒かかり、cold startだけでは説明できない。Gemini providerの待機または生成速度が候補。GPTは約3秒で、ユーザー報告と整合した。アプリ要求は`variant`/`reasoningEffort`を指定していないが、これが遅延差の原因かは未確認。
- 事前チェックポイント: 既存の効果音変更と計測計画を`c6a347b feat: play activation sound on recording start`としてコミットしてから、以下の性能実装に着手した。
- `/prompt_async`とSSE `/event`の実測は、OpenCode 1.18.34・同一profile/auth・`agent=build`・実アプリの`TranscriptRewritePrompt.SystemMessage`と同じ75文字の合成音声文で実施。system 490文字、user 75文字。3モデルに各1回の非同期要求を送信し、初回テキストと完了を測った。生成本文は記録していない。

| モデル | 非同期TTFT | 非同期完了 | 同期`/message`確認 | session作成/削除 |
|---|---:|---:|---:|---:|
| GPT 6 Luna Fast | 95 ms | 2.322 秒 | 2.611 秒・成功 | 28/10 ms（非同期）、38/32 ms（同期） |
| Muse Spark 1.3 Contributor Free | 38 ms | 3.226 秒 | 1.484 秒・モデル応答エラー | 11/13 ms（非同期）、26/17 ms（同期） |
| Gemini 3.5 Flash Lite | 52 ms | 1.993 秒 | 2.937 秒・成功 | 6/21 ms（非同期）、27/14 ms（同期） |

- 計測比較: 非同期3モデルに同じプロンプトを使った結果、最初の出力は38–95 msで届き、生成全体は1.99–3.23秒だった。同期方式でもGPTは2.61秒、Geminiは2.94秒。Muse同期要求はモデル応答エラーになったが非同期要求は成功したため、現方式を切り替える根拠にはなる一方、1回ずつのため性能・信頼性の優位を一般化しない。
- 前回の同期計測はGPT 3.943/2.772秒、Muse 43.462/2.925秒、Gemini 75.295/44.068秒。session create/deleteは7–30 ms、server health 1.06秒、provider一覧取得2.74秒だった。今回の全プロンプトでTTFT/完了が速かったこととの大きな差は、providerのcold状態・負荷・待機などの変動が候補。OpenCodeローカルログには2026-10-13 19:17:58 UTCにHTTP 429の記録が1件あったがprovider/modelとの対応は取れず、特定の遅延原因とは断定できない。音声/VAD/貼り付けや実ユーザー発話は計測していない。
- profile確認: `build` agentに固定model/optionsはなく、指定モデルのprovider既定を使う。OpenCodeから見えるvariantはGPT `high,low,max,medium,none,xhigh`、Muse `high,low,medium,minimal,xhigh`、Gemini `high,low,medium,minimal`。Better Flowはvariant/reasoning effortを送っていないため、実効effort値は特定できない。
- 実装: 同期`POST /session/:id/message`を、同じ1回のモデル要求を保つ`POST /session/:id/prompt_async` + SSE `/event`受信へ変更。初回テキストを記録し、session idle後に最終assistant応答を取得する。キャンセル/失敗時はOpenCode側の生成をabortし、sessionを削除する。各AI要求のgate待ち・model確認・session作成・prompt受付・TTFT・完了待ち・session削除・文字数・成否/失敗分類を`AppPaths.Root\opencode-timing.jsonl`へ記録する。本文/音声/プロンプト/出力/認証値は記録しない。ログは256 KiBでクリアされる。
- 対策判断: 固定のsession費用では大きな遅延を説明できず、隠れたwarmup、model変更、effort指定は行わない。連続要求でも遅い場合は、生成ログを見てprovider/API遅延かTTFT後の生成かを分ける。
- Release build/publish成功（警告0・エラー0）。`artifacts/publish-opencode-async/BetterFlow.exe`と`dist/Better Flow.exe`は81,871,385 bytes、SHA-256 `A82D27F775ADD21EE2AB1B99E85DD7DF538CD7BB41F8C8E1C0B067A58F951E6A`で一致。更新前は`artifacts/BetterFlow-before-opencode-async.exe`へ保存。dist内はBetter Flow.exeのみ。配布先を使っていた完全一致プロセスだけを停止して更新した。
- Gemini要求はこの追加計測で計3回、各要求の間隔を1分以上空け、再試行やバックグラウンド要求は行わなかった。テストスイートは実行していない。新EXEの実UI/実音声経路と`opencode-timing.jsonl`への初回記録は未確認。
- 根拠: [OpenCode Server API](https://dev.opencode.ai/docs/server/)は同期`/message`、非同期`/prompt_async`、server-sent event streamを掲載。[OpenCode Models](https://docs.opencode.ai/docs/models/)はmodel variants/optionsで推論effort等を設定でき、agent設定がglobal optionsを上書きし得ると説明する。

## 選択文編集キャンセル時の表示（2026-10-14）

- 選択文編集セッション中にショートカットでキャンセルした場合は、終了処理でインジケーターを即時非表示にする。通常の音声入力のキャンセル表示「キャンセルしました」は維持する。
- `MainWindow.xaml.cs`の選択文編集キャンセル開始時とセッション終了分岐だけを変更。Release build/publish成功（警告0・エラー0）。`artifacts/publish-selection-edit-cancel-status/BetterFlow.exe`と`dist/Better Flow.exe`は81,871,381 bytes、SHA-256 `B636B3467414D1D98F638CB52324E92B01D3CC53056811AE90E900293B317503`で一致。更新前EXEは`artifacts/BetterFlow-before-selection-edit-cancel-status.exe`へ保存。既存配布先EXEと完全一致したPID 39320だけを停止して置き換えた。
- `git diff --check`成功。テストスイートは未実行。実際の編集キャンセル操作と画面表示は未確認。

## 選択テキスト質問の回答表示と文脈安定化（2026-10-14）

- 回答ウィンドウはユーザー発話・過去の回答を履歴として描画せず、最新AI回答だけを表示する。新しい質問を検出した時点で前回答を隠し、回答テキストが届き始めてからウィンドウを表示する。
- GPT Liveはユーザーの質問が届く前の初期応答を表示対象から除外する。音声文字起こしイベントは応答・項目単位に蓄積し、短い`added`スナップショットや複数テキストチャンネルで表示全文が巻き戻らないようにする。
- 選択文はOpenCodeへJSONデータの`selected_text`として渡し、選択文を回答の第一根拠として優先するように指示する。OpenCodeへ送る会話履歴は直近4往復・約4,000文字以内に制限し、長時間の会話で選択文が履歴に押し流されるのを防ぐ。UI Automationではフォーカス要素が対象前景ウィンドウに属するかを確かめてから選択内容として採用する。
- Release build/publish成功（NU1900はNuGet脆弱性フィードへの接続警告のみ）。`artifacts/publish-selected-text-answer-fix-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,404,719 bytes、SHA-256 `33FF9B705B169389B681A2D1FCF347A212E9092E4718029650B3E89DF3C3482B`で一致。更新前の旧EXEは`artifacts/BetterFlow-before-selected-text-answer-fix-20261014.exe`へ保存。配布先を保持していたPID 17188は実行パスが`dist/Better Flow.exe`と一致することを確認して停止した。実GPT Liveイベント、実際の選択テキスト読み取り、モデル応答は未確認。テストスイートは実行していない。

## 質問モード終了とGPT Live回答ストリーム修正（2026-10-14）

- 質問モードの終了キーを押した時点で、OpenCode/GPT Live両方の回答ウィンドウを即時リセットして閉じる。セッション後始末でも再度閉じ、停止タイミングによる残留を防ぐ。
- GPT Liveの回答連結では日本語チャンク間に自動スペースを挿入しない。ASCII単語境界だけを補い、出力/コンテンツindexで項目順を並べる。複数文字起こし系列が切り替わるときは既に表示した先頭を保持し、完了イベントの全文で確定する。
- Release publish成功（NU1900: NuGet脆弱性フィード接続警告のみ）。`artifacts/publish-gpt-live-transcript-stability-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,405,594 bytes、SHA-256 `E943163B83128504047CB5C0758B456723646CD67293ACAB51EEDCEE8E9C70AB`で一致。更新前EXEは`artifacts/BetterFlow-before-final-gpt-live-transcript-stability-20261014.exe`へ退避。差し替え中に実行パスが完全一致したPID 49188を停止し、旧版ロックが残ったため以前の版も`artifacts/BetterFlow-old-live-after-replacement-lock-20261014.exe`へ退避して置換した。実GPT Live配信とホットキー操作は未確認。テストスイートは実行していない。

## GPT Live先頭欠落と質問回答の表示維持（2026-10-14）

- 発話確定前に届くGPT Live応答イベントを、音声入力開始イベントから質問文字起こし完了まで内部バッファへ保持する。質問確定時に先行回答があればそれを引き継いで表示し、セッション開始時の未依頼応答は引き続き表示しない。
- OpenCode/GPT Liveとも次の質問を処理している間は前回答を表示し続け、新しい回答テキストが届いた時点で同じダイアログ内を置き換える。回答ダイアログ右上の閉じるボタンを削除する。
- Release publish成功（NU1900: NuGet脆弱性フィード接続警告のみ）。`artifacts/publish-question-dialog-stream-fix-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,405,780 bytes、SHA-256 `B35CCD60E16DA2FB877B7FC10D6C76B9FE456C7B18C8FD1E02B43F32CCC04544`で一致。更新前EXEは`artifacts/BetterFlow-before-final-question-dialog-stream-fix-20261014.exe`へ退避。実GPT Liveイベント、ホットキー操作、UI表示は未確認。テストスイートは実行していない。

## 選択文編集の置換と連続認識の修正（2026-10-14）

- 変更前チェックポイント: `d3e6842 chore: checkpoint before selection edit fix`。
- `PasteService`の貼り付け入力を`keybd_event`から`SendInput`のCtrl+Vイベント列へ変更。SelectionEditServiceでは貼り付け前に元の選択範囲を確認し、貼り付け後に置換テキストの選択状態を確認してから編集成功とする。UI Automation要素が編集で再生成された場合は、同じ前面ウィンドウ内の編集可能な入力欄に限定して再選択を試す。
- 置換を確認できず元の選択範囲が残っている場合は、テキストを成功扱いせずクリップボードへのコピーを案内しつつ連続録音を維持する。置換後の選択状態が確認できた場合は同じ録音セッションで次の発話を受け付ける。
- Release build/publish成功。NuGet脆弱性フィードへの接続に関する`NU1900`警告のみ。`artifacts/publish-selection-edit-replace-continue-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,406,518 bytes、SHA-256 `9304809875C31B6BB8F359BDC74855E51F2C4183AA25F4D829E0AD35C915BB83`で一致。更新前EXEは`artifacts/BetterFlow-before-selection-edit-replace-continue-20261014.exe`に保存し、フルパスが一致するPID 33140を停止して更新した。
- 実マイク、OpenCode応答、対象アプリでの置換・連続発話は未確認。テストスイートは実行していない。

## 選択文編集の再発症状：原因調査と修正（2026-10-14）

- 読み取り専用でソース制御フローを確認。`ReplaceCurrentSelectionAsync`は貼り付け前に`WaitForSelectionTextAsync`で、前面ウィンドウと同一RuntimeIdのUI Automation選択を短時間再確認する。この確認が失敗すると`PasteService.CopyAndPaste`を呼ばずに`Applied=false, CanContinue=false`を返す。クリップボード書き込みは`CopyAndPaste`内にあるため、この経路では生成文がコピーされない。呼び出し側は`CanContinue=false`で連続録音を停止してループを抜ける。報告された「置換されない・クリップボードにない・1回で終了」を一度に説明する回帰候補。
- 実行ログや対象アプリのUIAイベントは取得していないため、実際にこの分岐へ入ったかは未確定。ソース上の原因候補とユーザー報告の症状は一致する。
- 次の実装計画: (1) OpenCodeから非空の生成結果を得たら、貼り付け可否の判定より先にクリップボードへ保存する。(2) 貼り付け前の判定は、同じ対象ウィンドウと元の文章の維持を安全条件にし、Select後の厳密RuntimeId再照会を貼り付けの必須条件にしない。(3) 成功後は置換文の選択を復元し、回復可能なUIA確認失敗だけで連続録音を止めない。対象ウィンドウ変更や選択文の変化など誤貼り付けの危険時は貼り付けを中止する。(4) 必要なら音声・選択文を記録しない段階別診断で、AI応答、クリップボード、SendInput、選択確認、録音状態を切り分ける。
- 実装: 生成結果を貼り付け判定より先にクリップボードへ保存。対象ウィンドウ内のフォーカス中の編集可能なUIA要素を優先して再取得し、変化したRuntimeIdをコンテキストへ更新。選択内容が元の文章と一致しない場合は貼り付けず、対象ウィンドウが前面の間は連続録音を維持する。Ctrl+V送信後は挿入された文章を再選択できた場合だけ次の編集へ進み、選択復元に失敗してもUndoから文書範囲を再探索する。挿入文検索の次回検索範囲が一致箇所の末尾で潰れていた問題も修正。
- Release build成功。NU1900のNuGet脆弱性フィード接続警告のみ。単体EXEを`artifacts/publish-selection-edit-recovery-20261014/BetterFlow.exe`へ発行して`dist/Better Flow.exe`を更新。両者82,406,776 bytes、SHA-256 `BB1666D95BA2AF2E37DC6DD4DDA084EB980580133D181D830B21F7B98F51A9DE`で一致。置換前EXEは`artifacts/BetterFlow-before-selection-edit-recovery-20261014.exe`に保存。対象アプリの実行プロセスは見つからなかったため停止していない。
- Codex/Chrome上での実入力、マイクを使った連続編集、Undoの実動作は未確認。テストスイートは実行していない。

## 選択解除・未置換の再調査と修正計画（2026-10-14、実装前）

- 最新のユーザー報告: AI編集後に選択が解除され、入力文章は置換されない。生成文がクリップボードへ保存されることは確認済み。現在の配布EXEのハッシュは前回発行物と一致するが、調査時点で対象の実行プロセスは見つからず、ユーザーの試行時の実行パスは確認できていない。
- 追加報告: インジケーターは「AI編集中」から直接「選択文編集中」へ移り、エラーもUndoボタンも出ない。生成結果が元文と異なる場合、この表示は`Applied=false / CanContinue=true`の回復可能な置換失敗経路と一致する。どの返却分岐かは未確定。
- ソース確認: クリップボード保存は貼り付け判定より前に行われるため、コピー成功だけではCtrl+Vまで到達した証拠にならない。現在の`SetFocus()`後の再確認は、選択が消えた場合に保存済み範囲を復元せず戻る。さらに貼り付け前の`Select()`でも選択変更が起こり得る。どちらで実際に選択が消えたかの段階別ログがなく、実行経路は未確定。
- 対象識別の問題: `FindFocusedEditProvider`は保存RuntimeIdを使わずに見つかった最初の編集可能なTextPatternを優先し、元の入力欄との対応を確認する前にRuntimeIdを上書きする。編集可能判定は祖先のEdit/ValuePatternも使うため、テキスト取得用の子要素と実際にキーボードフォーカスを受ける入力欄が一致する保証がない。コンテキストに元の入力欄、選択範囲のクローン、前後の文脈や位置が保存されていない。
- 成功判定の問題: `SendInput`の送信件数を`Pasted`と扱う。再選択と元選択のどちらも確認できない場合でも`Applied=true`にし、`CurrentText`を更新してしまう。入力欄での実際の置換確認を成功条件に変更する必要がある。
- 失敗表示の問題: 呼び出し側は`CanContinue`だけで「選択文編集中」を表示するため、`Applied=false`でも同じ表示になる。詳しい失敗理由を書き込む`RuntimeStatus`はXAMLで非表示になっている。最新報告の「エラーが出ない」は成功の根拠にならず、失敗がユーザーへ伝わらない構造が確認できた。
- 計画1: クリップボード保存、対象再取得、フォーカス前後、Select前後、Ctrl+V送信、文書変更確認、再選択、録音状態を区別する内部診断を追加する。テキストや音声は保存せず、選択が空か、元文・置換文との一致、要素の種別とフォーカス可否、同じ入力欄か、送信件数、エラーコードだけを記録する。AIを呼ばない固定置換で、最初に失敗する段階を特定する。
- 計画2: テキストを読み取るUIA要素と実際の入力欄を分けて保持し、起動時に選択範囲のCloneと同一入力欄の前後の内容・境界を一時メモリーへ保存する。再取得は同じ入力欄と確認できた要素に限定し、別要素へRuntimeIdを無条件に付け替えない。既に対象入力欄にフォーカスと元選択がある場合はSetFocus/Selectを省く。フォーカス変更が必要な場合は、元の文書が変わっていないことを確認してから保存範囲を復元する。
- 計画3: Ctrl+Vの送信成功と入力文章の変更確認を別の結果にする。元の選択位置で置換が行われ、前後の文章が保持されたことを確認した後だけCurrentTextとUndoを更新する。未確認時は追加入力による二重貼り付けを行わない。確定した置換範囲をUIAで一括選択し、同じ入力欄・範囲の維持を確認する。
- 計画4: 編集・再選択・次の録音受付を状態として区別する。回復可能な選択取得失敗ではマイクを維持しつつ次のAI編集を保留し、同じ対象範囲を復元できた後に継続する。置換失敗は短いインジケーターで明示し、`CanContinue=true`だけで正常な待機表示へ戻さない。貼り付け失敗や未確認を成功扱いしない。実装後はCodexとChrome/ChatGPTで初回置換、2回目の発話、選択維持、Undo、重複文、改行、絵文字、アプリ切り替えとキャンセルを送信なしのテスト文で確認する。
- この節は計画時点の記録。次節に実装内容と検証結果を記録する。

## 選択範囲が消えて置換されない問題の修正結果（2026-10-14）

- 変更前チェックポイント: `1697b77 docs: checkpoint selection replacement investigation`。
- 実装: 選択したTextPattern要素と選択範囲のCloneを編集開始時に保持。生成後は保存した要素・RuntimeIdを先に使い、本文選択が既に一致しているときは`SetFocus()`や重複した`Select()`を呼ばない。クリップボード操作でフォーカスツリーが変化しても、保持済みTextPatternで範囲を再確認し、元の要素が一致し範囲内容も変わっていない場合だけ選択を復元する。
- 実置換の確認: `SendInput`のイベント送信件数と実際の置換結果を分離。置換後の確認はWPF UIスレッド外で行い、文書全体の検索ではなくカーソル直前の範囲から生成文を照合する。生成文が実際に選択状態になった場合だけ`Applied=true`、編集コンテキスト更新、Undo表示を行う。それ以外は成功扱いせず、インジケーターに「置換失敗」を表示する。
- 診断: `selection-edit-diagnostics.jsonl`へ選択内容・音声を保存せず、段階、UIA要素種別/フォーカス属性、選択状態、SendInput件数、Win32エラー番号だけを記録する。ログは128KBでクリアして上限を保つ。
- 合成テスト: WPF TextBoxで選択をいったん解除したあと、固定文を2回連続置換。本文と各置換後の全選択を確認し成功。WebView2 textareaでも初回・連続置換の両方で本文と選択範囲を確認し成功。実際のCodex/Chrome画面、マイク入力、OpenCode生成は未確認。
- Release build/publish成功。NuGet脆弱性フィードへ接続できない`NU1900`警告のみ。`artifacts/publish-selection-range-final-20261014/BetterFlow.exe`と`dist/Better Flow.exe`は82,411,974 bytes、SHA-256 `791E70EDF77822C903D79EA1A08DCEEF78879B9E3FDA65CBF19CF91B01A141F0`で一致。変更前EXEを`artifacts/BetterFlow-before-selection-range-fix-20261014.exe`、最終ビルド直前の中間EXEを`artifacts/BetterFlow-before-caret-relative-reselection-20261014.exe`へ保存。配布EXEのプロセスは実パスを確認して停止した。

## 質問モード回答の選択・コピー対応（チェックポイント、2026-10-14）

- 要件: 質問モードの回答全文をユーザーが選択し、通常のコピー操作でテキストとしてコピーできるようにする。回答内容は編集不可にする。
- 調査: AnswerWindow.xamlは回答をTextBlockで描画し、回答ウィンドウにはWS_EX_NOACTIVATEを設定している。TextBlockはテキスト範囲の選択に対応せず、現状のウィンドウ設定も入力フォーカスを取れない。
- 実装方針: 回答表示を読み取り専用かつ折り返し可能な選択テキストコントロールへ変更し、回答ウィンドウは表示時に背面アプリのフォーカスを奪わず、ユーザーがクリックした際は選択・コピー操作が可能な設定にする。回答ストリーミング、スクロール、表示タイミングは維持する。
- このチェックポイントはソース変更前の状態を記録する。

## 質問モード回答の選択・コピー対応（実装結果、2026-10-14）

- 実装: 回答表示を読み取り専用のTextBoxへ変更し、折り返し・既存の外側スクロール・配色を維持。TextBoxは編集不可で、マウス選択と標準のCtrl+Cコピーを使える。アプリが回答を表示した時点では背面アプリのフォーカスを保ち、回答欄をクリックした時だけ回答ウィンドウが通常どおりアクティブになるよう、WS_EX_NOACTIVATEを外してツールウィンドウ属性だけ残した。
- ストリーミング中に回答欄へフォーカスがあり、表示文が同じ接頭辞のまま伸びる場合は、全選択・選択範囲・末尾カーソルを可能な範囲で維持する。
- Release build成功（0エラー、NU1900はNuGet脆弱性フィードに接続できない警告）。win-x64自己完結型単体EXEを発行し、発行物とdist/Better Flow.exeは82,412,139 bytes、SHA-256 2AD56BBA8BD32F4617C8BE8469624C8BBF0AD30E206B256EA753E10817AC8E9Aで一致。更新前EXEはrtifacts/BetterFlow-before-selectable-answer-20261014.exeへ保存。AnswerWindow.xaml/AnswerWindow.xaml.csの画面操作テストは行っていない。

## 通常音声入力の疑似ストリーミング文字起こし（2026-10-14）

### 現状
- 通常入力は MicrophoneRecorder が16kHz・16-bit・mono の音声を100ms単位で受け、MemoryStream に蓄積する。録音中は音量だけを IndicatorWindow に通知する。
- 停止時に録音全体を float 配列へ変換し、AsrService の Sherpa.Onnx OfflineRecognizer で一度だけ認識している。Parakeet TDT CTC 0.6b INT8 を使う現在のモデルと最終文字起こし経路は維持する。
- IndicatorWindow は画面下の小型ピルに状態とマイク波形を表示する。テキスト表示領域はない。

### 実装方針
1. MicrophoneRecorder に録音中の音声スナップショット取得を追加する。録音コールバックでは保存だけを行い、認識処理は走らせない。音声のコピーはロック内で短時間に行い、PCMからfloatへの変換と推論はバックグラウンドで行う。一時スナップショットは使い終わったら消去する。
2. 通常音声入力の録音開始時だけ、疑似ストリーミング処理を開始する。発話が約1秒以上たまってから初回認識し、その後は現在の録音全体を順次認識する。次の認識までの待ち時間は最低1.5秒とし、直前の推論時間に応じて最大10秒まで延ばす。処理中に到着した複数スナップショットはキューへ積まず、認識器への同時呼び出しを避ける。停止後は実行中プレビューの完了を待ってから最終認識へ進む。古い録音セッションの結果はセッションIDで破棄する。
3. 各途中結果は全文スナップショットとして扱い、前の文字列へ単純に連結せず表示全体を置き換える。認識結果が空や記号のみなら既存表示を消さない。途中結果は表示専用とし、貼り付けやOpenCode送信には使わない。
4. IndicatorWindow のピルの上に、小さな角丸の濃色テキストカードを追加する。初回の有効な途中結果が得られたときだけ表示し、ピル自体は画面下端から約12pxの位置を保つ。カードは幅約380px・高さは数行までとし、マイク波形と録音状態表示は維持する。表示更新はUIスレッドへディスパッチする。
5. ホットキー停止時は途中認識の新規受付を止め、実行中の推論の結果が古い表示へ反映されないようにする。その後、現行どおり録音全体で最終認識を1回行い、その結果だけを文字起こしモードの貼り付け、またはAIモードのOpenCode整形へ渡す。成功・失敗・キャンセル・終了の全経路でプレビューを片付ける。選択文の編集・質問・GPT Live の録音経路には適用しない。

### 表示と負荷の注意点
- Parakeet は現在オフライン認識器なので、単語単位のモデルストリームではない。画面の更新は録音済みスナップショットを再認識した後に行われ、認識が進むと先頭側の語句が修正される場合がある。最終認識結果を正として再表示し、最終貼り付けに使う。
- 録音全体を繰り返し推論するため、長い録音ほどCPU負荷が増える。推論は同時に1件だけ実行し、各推論後に最低1.5秒、直前の推論時間に応じて最大10秒待ってから最新の録音スナップショットを取得する。待ち行列は作らない。さらに負荷が大きければ、次段階でSilero VADの発話区間単位の推論を検討するが、チャンクの連結による重複・欠落を別途扱う必要がある。

### 実装内容と完了条件
- MicrophoneRecorder に録音バッファの安全なスナップショット取得を追加。音声 callback は従来どおり保存だけを行う。
- 通常録音専用の TranscriptPreviewWindow を追加。既存の下端インジケーターと波形を維持し、その上に角丸の濃色カードで途中結果を表示する。
- 途中結果は表示用の全体置換に限定し、認識器は常に1件ずつ実行する。停止時はプレビューをキャンセルして終了を待ち、Parakeet の既存認識器で録音全体を再認識する。最終結果だけが既存の貼り付け／AI整形へ進む。
- 発話中に途中文字起こしがインジケーター付近へ順次表示され、更新時に同じ文が重複連結されない。
- 停止後の最終認識結果だけが既存の貼り付け／AI整形へ進む。録音中の途中結果は入力欄を変更しない。
- AIモードのオン・オフ、キャンセル、認識失敗、通常音声以外の各モードで表示が残留せず、既存の録音・波形・状態表示が保たれる。
- Release build 成功（0エラー）。NuGet 脆弱性フィードの接続警告あり。win-x64自己完結型単体EXEを発行し、発行物とdist/Better Flow.exeが82,414,182 bytes、SHA-256 FC63049D821ED4B304A02F6E99A5E1171133B0709AD55A94354C35F085484947で一致。更新前EXEはartifacts/BetterFlow-before-transcript-preview-20261014.exeへ退避。テスト実行および実機の録音・表示確認は未実施。

## 音声入力からの送信コマンド（2026-10-14）

- AIモードの通常音声入力に限り、元の発話末尾に「送信して」または「送って」があれば命令語を除いてAI整形へ渡す。さらにAI整形中はSilero VADとローカル文字起こしで短い音声を監視し、単独の「送信して」「送って」を検出する。監視音声はOpenCodeへ送らない。文字起こしのみ、選択文の編集・質問モードには適用しない。
- AI整形が成功して入力欄へ貼り付けられた後、約150ms待ち、録音開始時の対象ウィンドウが前景にあることを再確認してからEnterを送る。AI失敗・貼り付け未確認・対象フォーカス喪失時はEnterを送らない。監視用録音はAI処理終了時に停止する。
- Release build/publish成功（0エラー）。NuGet脆弱性フィード接続警告（NU1900）あり。win-x64自己完結型単体EXEの最終発行物とdist/Better Flow.exeは82,416,844 bytes、SHA-256 13ADF8F9BDCA0FC4A2856956A9982A6008EC0E4F455DDBD9B91FC343369EED57で一致。更新前EXEはartifacts/BetterFlow-before-voice-send-final-20261014.exeへ退避。実マイク認識とEnter送信は未確認。

## 音声AI整形用の用語辞書（2026-10-14）

- 実装前チェックポイント: `a65e658 feat: checkpoint transcript preview and voice send`。前回までの作業状態を先に保存してから変更を開始。
- 設定画面で正しい表記と任意の誤認識・別表記を手動登録、更新、削除できる。設定ファイルに保存し、登録語は通常音声入力のAI整形リクエストに自動で含める。選択文の編集・質問や文字起こしのみモードには適用しない。
- AIには辞書項目を表記補正データとして扱わせ、登録済み別表記を文脈に応じて正式表記に寄せる。辞書にない固有名詞は推測で別名に置き換えず、発話にない事実・説明も追加しないよう指示する。自動学習による誤った語の定着を避けるため、辞書への追加自体は手動登録とする。
- Release build/publish成功（0エラー）。NuGet脆弱性フィード接続警告（NU1900）あり。win-x64自己完結型単体EXEの発行物とdist/Better Flow.exeは82,421,379 bytes、SHA-256 541D1133CC089DDC0131591E597AA4E6937FB36D9726ACC60E049ACA5FB62066で一致。更新前EXEはartifacts/BetterFlow-before-correction-dictionary-20261014.exeへ退避し、実行パスが一致したPID 41948を終了。実マイク認識とモデルが登録語を正しく補正するかは未確認。

## 送信時の手修正から用語辞書へ自動登録（2026-10-14、実装完了）

- ユーザー要件: 通常音声入力で貼り付けた文章を手で修正して送信した際、修正した用語の**正しい表記だけ**を既存辞書へ自動登録する。誤表記・別表記は保存せず、辞書の内部データとAI整形プロンプトにも含めない。設定画面から「誤認識・別表記」入力を削除する。精度・遅延に問題が出た場合すぐ切り替えられるよう、自動学習と判定方式を設定から変えられるようにする。
- 変更前チェックポイント: 当初の計画用に`9e3ee46 chore: checkpoint before automatic dictionary learning`、初版計画を`0aa5a2f docs: plan dictionary learning from submitted corrections`として保存。実装前には改訂計画のコミット後、別途ソース変更直前のチェックポイントを作成する。
- 確認済みの現状: `PasteAndShow`はクリップボード保存とCtrl+Vの送信結果だけを返し、その後の入力欄の文章・ユーザー送信を追跡していない。`CorrectionDictionary`は正式表記と別表記を保存し、通常音声入力のAI整形へ渡す。UI AutomationによるTextPattern/ValuePattern利用と対象ウィンドウ・要素RuntimeIdの照合は選択文編集に既存実装がある。

### 推奨する処理の流れ

1. 貼り付け対象の識別と音声由来範囲の追跡
   - 通常音声入力だけを対象に、対象ウィンドウ、入力欄のUIA要素/RuntimeId、貼り付け前後の本文、実際の挿入範囲、元の最終文字起こし、貼り付けた最終文章をメモリで保持する。Ctrl+V送信件数に加え、本文へ生成文が挿入されたことを確認してから追跡を開始する。
   - 元々入力欄にあった文と音声で挿入した部分を区別する。同じ入力欄へ複数回音声入力する場合も挿入範囲を更新・統合し、元々の文の修正を学習しない。入力欄の再生成・文書全体の変更などで範囲の対応が曖昧になった場合は、その記録を破棄する。
   - 対象入力欄のTextChanged/Value変更イベントを専用MTAワーカーで読み、最新の送信前スナップショットを保持する。低レベルキーボードフック内でUIAを呼ばず、アプリの入力キーを消費しない。イベント未対応時だけ対象欄への頻度を制限した読み取りを補助に使う。別ウィンドウの本文、パスワード欄、質問・選択文編集を対象へ広げない。保持内容は上限と有効期限を設け、終了時に破棄する。
2. 送信を確認してから候補を確定
   - Enter/Ctrl+Enterと、対象入力欄に関連する送信ボタン操作を送信候補として扱う。Shift+Enter、IME確定、キーリピート、Better Flow自身の合成Enterを区別する。
   - 候補操作前の最終本文を保ち、操作後に同じ入力欄の本文が送信で空になった等の対応アプリの変化を確認してから学習へ進む。UIAだけでは任意アプリのサーバー受理まで証明できないため、確認の意味は「対象入力欄の送信操作と送信後のUI変化」に限定する。単なる削除・改行・フォーカス移動を送信扱いしない。
   - CodexとChrome/ChatGPTの入力欄を最初の対応確認対象にし、送信で本文を消さないアプリや送信ボタンとの関連が特定できないアプリでは推測登録しない。学習失敗はユーザーの送信を妨げない。
3. 文字差分を用語の補正候補へ変換
   - 音声由来部分の貼り付け時本文と送信直前本文をローカルで比較し、複数の独立した修正を抽出する。日本語・英語・絵文字・改行を扱い、計算量に上限を置く。
   - 共通の文字を削って一文字の対だけにしない。例: 「オープンコート」→「オープンコード」は単語全体を候補にする。句読点・空白だけの変更、単純な追記・削除、長い文章の書き直しは候補から除く。
   - 辞書へ入れる値は送信された修正後の「正しい表記」だけにする。貼り付け時の誤表記、元の文字起こし、差分の旧側テキストは候補の判定に必要な間だけメモリに置き、辞書・設定・保存ログへ書かず、登録後すぐ破棄する。
4. 意味変更の誤学習を防ぐ
   - AIモードONでは、差分がある送信だけ、選択中のOpenCodeモデルに用語・表記訂正候補の判定をバックグラウンドで依頼する。複数修正を1リクエストへまとめ、差分と必要な短い前後文脈だけを送る。返す対応は旧文・新文に実在する語句へ限定し、モデルが新しい語を創作した結果を採用しない。日付・数量・意図の変更や文体変更は学習候補から除く。
   - AIモードOFFでは追加のモデル呼び出しを行わず、ローカルで明確に判定できる短い表記変更だけを採用する。判定が曖昧な修正は見送る。学習方式を設定から「速度優先（ローカル判定）」と「精度優先（選択中のOpenCodeモデルで候補を一括判定）」に切り替えられるようにする。精度優先でもAIモードOFFまたはモデル未準備なら新たな起動・待ち時間を作らず、ローカル方式へ切り替える。
   - 学習は送信経路と切り離す。変更なしなら追加リクエストなし、修正送信1件につき最大1回、自動再試行なし。連続修正はまとめ、学習用要求は最大2回/分に制限する。OpenCodeモデル切り替えや音声処理と競合しない専用の処理状態/キャンセル管理を設け、失敗で音声入力側のOpenCodeを停止しない。
5. 既存辞書へ安全に保存
   - 辞書のデータを正式表記だけの構造にする。設定UIの「誤認識・別表記」欄を削除し、AI整形用プロンプトも正しい表記の一覧だけを渡す形式へ変える。現在の設定ファイルにある`PreferredSpelling`は保持し、旧`Alternatives`は読み捨てて次回保存時に消す。既存の手動登録語と自動候補が競合したら自動上書きしない。登録数の上限を保ち、上限を超えても古い正しい表記を削除しない。
   - 設定の保存はUIスレッドへ集約し、モデル選択・マイク・AIモードなど同時変更を古いスナップショットで巻き戻さない。保存失敗時は辞書変更だけを戻す。
   - 自動登録は既定ONのスイッチで即時停止可能にする。判定方式も同じ設定画面でいつでも切り替え可能にし、遅延が問題ならローカル方式へ、誤登録が問題なら精度優先または自動登録OFFへ変更できるようにする。既存の手動追加・削除を維持し、自動登録語も同じ一覧で削除可能にする。保存するのは正しい表記だけで、誤表記、入力欄全文、送信履歴は保存しない。
6. 実装後の確認と配布
   - 差分・範囲対応・既存辞書統合を固定文で確認する。複数修正、単語途中の訂正、ASRとAI出力の表記差、元からある文章、複数回音声入力、改行、絵文字、意味変更、上限、保存失敗を含める。
   - ローカルの入力欄でEnter/Ctrl+Enter/Shift+Enter、IME確定、送信ボタン、単なる全削除、フォーカス移動、UIAイベント遅延、入力欄再生成を確認する。Codex/ChromeのUIA取得と送信前スナップショットは実際の入力欄でも確認が必要。外部サービスへ自動テスト投稿することは今回の計画承認に含めない。
   - 改訂計画をコミットし、続けてソース変更前チェックポイントを作成してから実装する。Release build/publish、単体EXE差し替え、サイズ/SHA-256照合を行う。

### 技術資料

- UIAのイベント登録・解除と操作は専用MTAスレッドへ集約する: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading
- TextPatternの本文変更イベント: https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern.textchangedevent
- 低レベルキーボードフックは短時間で復帰し、入力を次のフックへ渡す: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/ms644985(v=vs.85)

### 実装結果

- 自動学習は既定ON、判定方式は既定で精度優先。設定のスイッチで機能全体を止められ、速度優先へ切り替えると追加のOpenCode判定を使わずローカルの限定候補だけを登録する。
- 音声入力文の貼り付け後、対象ウィンドウの編集欄だけをメモリ上で追跡する。Enter/Ctrl+Enterまたは関連する送信ボタンの操作と、送信後の欄の変化が確認できた場合に限り差分を調べる。曖昧な入力欄、選択文モード、音声送信コマンド、送信未確認の場合は登録しない。
- 設定モデルと整形プロンプトから別表記欄・旧表記データを除去。既存設定の`PreferredSpelling`は保持し、旧`Alternatives`プロパティの値は読まず、次回保存で項目ごと取り除く。辞書に保存・利用するのは送信された訂正後の正しい表記だけ。
- 差分判定は訂正後の語句だけを保存候補にする。速度優先の候補判定は限定的で、曖昧な日本語訂正を見送る場合がある。精度優先では修正送信に最大1回の一括OpenCode判定を行い、再試行なし・最大2回/分に制限。次の録音開始時は保留中の判定をキャンセルする。
- コミット: 計画改訂`885c543`、実装直前チェックポイント`d2a6b38`。実装コミットはこの結果記録を含む。
- Release buildとwin-x64 self-contained publish成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。発行物と`dist/Better Flow.exe`は82,431,195 bytes、SHA-256 `3028E88D5FF3034B5D72F822D6BC58FE72525DBFC316E2658FB4EEC5CB488532`で一致。更新前EXEは`artifacts/BetterFlow-before-canonical-dictionary-learning-20261014.exe`へ退避。更新時に実行パスが一致するアプリプロセスのみを停止。
- 実行時のCodex/Chrome入力欄UI Automation・送信検知、実マイク、OpenCode候補精度と遅延は未確認。テストスイートは実行していない。

## Better Flow — 自動辞書登録の診断性改善（2026-10-14）

- 再調査（分類: 実行設定確認・障害切り分け、出典: AutoPrompt/settings.jsonとopencode-timing.jsonl、確認日: 2026-10-14 Asia/Tokyo、信頼度: 保存設定とログ値は高、個別リクエストの用途は未確定）: AutoLearnDictionaryEnabled=true、DictionaryLearningMode=precision、AiModeEnabled=true、選択モデルはopenai/gpt-6-luna-fast、辞書件数0。直近のOpenCode計測ログには16:18:38 JSTの成功リクエストがあるが、既存ログは処理用途を記録せず、候補判定なのか文章整形なのか特定できない。OpenCode応答文字数と通信成功だけでは候補承認・解析・設定保存を確認できない。自動学習処理の例外も従来は握りつぶされていた。
- 改善: 入力欄の検出、貼り付け範囲の一致、送信操作、送信後の欄変更、候補抽出、精度/ローカル判定、承認件数、設定保存の段階と安全な理由コードを、本文・用語・ウィンドウ名なしでdictionary-learning-diagnostics.jsonlへ記録する。設定画面の辞書状態テキストにも各失敗段階を表示する。ログ上限128KB。
- 実装前チェックポイントはaebb4d6。Release build/publish成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。単体EXEは82,434,126 bytes、SHA-256 47CB7ACFA733F70B99F4326E9EF40B36F5A1917C9C4E20BB502F1CBAA986ACB4。配布EXEと発行物のハッシュ一致を確認し、更新前はartifacts/BetterFlow-before-dictionary-learning-diagnostics-20261014.exeへ退避。置換時点で完全一致する実行中アプリプロセスはなかった。
- 新ビルドのCodex/Chrome送信検出、辞書候補の判定と保存は未確認。テストスイートは実行していない。
- 追加調査で、音声の「送信して」経由では`sendAfterPaste`分岐が辞書監視の起動自体を除外しており、さらにアプリ自身が送るEnterは注入キーとしてキーボードフックから意図的に無視されることを確認。監視開始条件を自動送信にも広げ、監視準備完了を待ってから送信し、アプリ送信の成功を監視へ明示通知するよう修正した。
- 自動送信経路修正後のRelease build/publish成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。`dist/Better Flow.exe`と発行物は82,434,710 bytes、SHA-256 `C77AAD5007D7CC5A25864454C62D16DB2B42813F7868537577EBE19069390126`で一致。更新前EXEは`artifacts/BetterFlow-before-autosubmit-dictionary-fix-20261014.exe`へ退避。置換時に一致する実行中プロセスは確認されなかった。
- 自動送信・手動送信後に辞書項目が作られることは、次回の実利用で確認が必要。テストスイートは実行していない。
- ユーザー再試行後のログ（分類: 実行時障害の観測、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`・`settings.json`・`opencode-timing.jsonl`、確認日: 2026-10-14 Asia/Tokyo、信頼度: ログ値は高、入力欄での貼り付け実態は未確認）: 16:33:45.291に`monitor_started`、約49ms後の16:33:45.340に`pasted_text_not_found`で終了し、送信信号・候補抽出へ進んでいない。設定は自動登録ON・精度優先・AIモードON、モデルopenai/gpt-6-luna-fast、辞書0件。近接したOpenCode成功ログ（計3.2秒、応答21文字）は用途を記録しないため、辞書レビュー成功の証拠ではない。最有力原因は貼り付け直後のUI Automation読み取りが古い内容を返した競合だが、対象欄違い・貼り付け失敗はログだけでは除外できない。
- 対策: 対象欄で貼り付けた文章が見えるまで、最大2秒・50ms間隔で状態を再読し、見つかった時だけ追跡する。検出までの読み取り回数と即時/更新後の区別は、本文なしで診断ログへ記録する。Release build/publish成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。`dist/Better Flow.exe`と発行物は82,434,986 bytes、SHA-256 `129688AAA21C3B67ED0E89C670358FCC80232411D877FC91F55A6B5080AD0DEB`で一致。更新前版は`artifacts/BetterFlow-before-dictionary-paste-sync-20261014.exe`。置換ロックを持つPID 51480は実行パス完全一致後に停止した。
- 追跡再読込修正後の実使用による登録確認は未実施。テストスイートは実行していない。
- 次の試行ログ（分類: 実行時障害の観測、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`・`settings.json`・`opencode-timing.jsonl`、確認日: 2026-10-14 Asia/Tokyo、信頼度: ログ値は高、アプリ側での送信成否の細部は未記録）: 16:41:51.788に監視開始、16:41:51.830に貼付文を即時検出し追跡開始。16:41:56.972に物理Enter、16:41:57.451に送信検知したが、16:41:58.962に送信確認失敗で終了。候補抽出・モデル判定・辞書保存の記録はない。設定は自動登録ON・精度優先・AI ON、openai/gpt-6-luna-fast、辞書0件。
- 送信後の確認対策: Enter検知処理で最新の入力欄内容を取り直して訂正範囲を更新。UI Automationが空欄を空白/改行だけで返す場合も空欄と認識する。確認に失敗した場合は入力欄読み取り不可・変化なし・文章残存などの理由コードと試行数を本文なしで記録する。Release build/publish成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。`dist/Better Flow.exe`と発行物は82,435,194 bytes、SHA-256 `46C9884675624B3FCBEE47D0F953C30F1A883A8A5B5ED07EC903A680C3B0485B`で一致。更新前版は`artifacts/BetterFlow-before-submit-confirmation-fix-20261014.exe`へ退避。ロックを保持する完全一致パスのPID 53784を停止後、再試行コピー成功。
- 送信後確認修正後に候補が抽出・保存されることは未確認。テストスイートは実行していない。

## Better Flow — 自動辞書登録の失敗箇所の追加切り分けと保存通知（2026-10-14）

- 直近ログ（分類: 実行時障害、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`、確認日: 2026-10-14 Asia/Tokyo、信頼度: ログ上の段階は高、送信操作の成否は未確定）: 16:48:25.764に監視開始、16:48:25.867に貼り付け本文を検出、16:48:25.868に追跡開始。16:48:30.996にEnter、16:48:31.503に送信信号を検出したが、16:48:33.006に`submission_not_confirmed / field-unchanged`で終了。送信後のUI Automation読み取り8回で入力欄の変化が観測されず、候補抽出・モデル判定・辞書保存へ進んでいない。したがって今回の失敗は用語候補やOpenCodeモデルではなく、送信後の入力欄変化確認の段階で発生している。現ログだけでは、対象アプリが入力欄を保持したのか、UI AutomationのValuePattern/TextPatternの一方が古い値を返したのかは区別できない。
- 追加診断: 次回の`submission_not_confirmed`に、本文を含めず送信前の文字数とValuePattern/TextPatternそれぞれの状態（空・不変・変化・読取不可）を記録する。これで送信動作自体とUI Automationプロバイダーの古い読み取りを分けて調べる。
- 成功通知: 自動または手動で正しい表記を辞書へ登録した場合、録音インジケーターと同じカプセルを画面下中央に1.5秒表示する。用語の本文や誤表記は通知・ログへ出さない。
- `submission_not_confirmed`にValuePattern/TextPattern別の読取状態と文字数を追記する。内容本文・単語は記録しない。
- Release win-x64 self-contained single-file publish成功。NU1900のNuGet脆弱性フィード接続警告のみ。発行EXEと`dist/Better Flow.exe`は82,435,622 bytes、SHA-256 `417D55AE76903B6A8D6D89795C56E27EE740AE6C0C0A59800CB31B79E7BCA880`で一致。更新前は`artifacts/BetterFlow-before-dictionary-save-indicator-20261014.exe`へ退避し、実行パスを確認したBetter Flow PID 53552の停止後に置換。実機入力欄での再現と通知表示は未確認。テストスイートは実行していない。
- 同じ症状の再試行ログ（分類: 実行時障害、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`・`settings.json`、確認日: 2026-10-14 Asia/Tokyo、信頼度: ログ観測は高、スナップショット汚染の発生順序は未確定）: 17:06:28.801に監視開始、17:06:28.902に貼り付け追跡開始、17:06:35.123にEnter、17:06:35.555に送信信号検知。17:06:37.065の失敗診断は`field-unchanged;baseline-length=1;value-pattern=empty:length=1;text-pattern=empty:length=1`。比較基準は1文字で、失敗後の両UIA読取結果は空白1文字。設定は自動登録ON・精度優先・AI ON、選択モデル`openai/gpt-6-luna-fast`、辞書0件。
- レビューによる原因説明の訂正: 送信信号を受けた後のループで`_submitSegment`/`_submitFullText`を再読込値で上書きする経路はソースから確認でき、送信後の空欄が比較基準へ混入する競合を起こせる。一方、イベントハンドラーも空白を通常の追跡スナップショットへ採用できるため、今回の汚染が信号前か後かは現ログだけでは確定しない。`_prefix`/`_suffix`が両方空だったという前回の断定もログに根拠がなく撤回する。両方空、または周辺が空白だけの場合に、空の音声範囲を有効として扱う実装上の問題は確認できる。
- 最新の追加ログ（分類: 実行時障害、出典: 同診断ログ、確認日: 2026-10-14 Asia/Tokyo、信頼度: 観測は高、Enterの実用途・UIA読取例外の発生箇所は未確定）: 17:14:54〜17:15:03に4回のEnter信号があり、`field-retained-text`（基準42文字→診断43文字、44→48）、`field-unchanged`（47→47）、`editor-unreadable`（基準47文字、最終ValuePatternは空、TextPatternは読取不可）になった。空白基準への上書きだけでは全事例を説明できない。EnterはIME確定・改行・実送信の候補にすぎず、要素情報取得とパターン取得の失敗も区別する必要がある。候補抽出・モデル判定・辞書保存には未到達。

### レビュー後の修正計画（実装前）

1. **送信候補ごとに送信前の記録を固定する。** 追跡本文・音声範囲・読取時刻・版番号・有効/空欄/範囲喪失/読取失敗の状態を保持する。送信信号時にその記録を一つの不変データへコピーし、以後のUIAイベント・ポーリング・二重の送信信号で上書きしない。単に「最後に読めた文章」を固定するだけでは、最後の手修正が未取得だったり削除前の本文が残ったりするため、最新読取状態と時刻を検証し、不確かな送信前本文は学習しない。キーボードフック内ではUIAを呼ばない。
2. **空欄を無視せず、本文とは別の状態として記録する。** 空白だけの音声範囲を修正文として採用しない。同時に、その読取を捨てて以前の本文を有効に残すこともしない。送信信号より前の全文削除は有効記録を失効させ、同じ追跡範囲に文章が再入力されれば最新状態を更新する。送信信号後の空欄は、固定した送信前本文との比較によるクリア確認に使う。信号とUIA読取の順序を判別できない場合は推測登録しない。
3. **Enterを送信候補として扱い、実送信の確認まで監視を維持する。** キーリピート・Shift+Enter・IME確定を区別し、信号時の対象ウィンドウと編集欄の対応を確認する。IME状態を確実に判定できない場合も、Enterだけでは学習しない。送信確認前に「修正なし」で監視を終了させない。改行・入力継続・未確認の送信候補では、古い候補を破棄し、本文の最新状態を追跡して次の送信を待つ。
4. **アプリ自身のEnter送信も、送信前に記録を準備する。** 現在の`NotifyApplicationSubmit`は`SendEnter`成功後に呼ぶため、空欄イベントの後に記録を採る競合が残る。注入前に追跡準備と送信前記録を確定し、注入成功後にその候補を確認へ進める。注入失敗・対象フォーカス不一致は学習候補を取り消し、UIクリア確認は別途行う。
5. **入力欄のクリアと読取失敗を混同しない。** `!TryExtractSegment`は周辺文章の変更・範囲喪失でも発生するため、それだけを送信後クリアの証拠にしない。同じ編集欄が空になった、または元の前後文を保って音声範囲だけが空になった場合を確認する。読取失敗時は要素メタデータ取得/ValuePattern/TextPatternと例外種別を分け、同一ウィンドウ・同一編集欄を検証してパターンを再取得する。再生成された欄は対応アプリで同じ入力欄との関連を特定できる場合だけ再関連付けする。要素消失・別の空欄・フォーカス移動を送信成功とみなさず、パスワード判定を省略してフォールバックしない。タイムアウト延長を主対策にしない。
6. **安全な診断と確認条件を用意する。** 監視ID・送信候補ID、信号時/送信後の文字数・空欄状態・スナップショット版/経過時間・読取段階/例外種別を記録し、本文・誤表記・正しい用語・ウィンドウ名は残さない。UIA順序の再現確認では、正常な修正送信、信号直後クリア、遅延イベント、最終修正の未反映、全文削除してEnter、IME確定、Shift+Enter、二重信号、UIA要素失効、音声の自動送信を確認する。実利用ではCodex/Chromeの修正後本文が候補へ渡ること、正しい表記が設定一覧へ保存されること、保存成功時だけ既存の下中央カプセルが表示されることを確認する。ローカルな競合確認でモデル要求を増やさず、モデル実確認は少数回に限定する。

- 今回は計画と原因説明を修正した。アプリコード・EXEの変更やテスト実行は行っていない。前回の通知・診断差分と今回の計画改訂を実装前チェックポイントとして保存する。自動辞書登録の根本修正は次の実装対象。

### レビュー計画の実装結果（2026-10-14）

- 送信候補ごとに本文・音声範囲・版・取得時刻・状態を固定し、送信信号後のUI Automation読み取りで候補を上書きしないよう変更。空欄、範囲喪失、読み取り失敗を別状態にし、空欄を有効な訂正文や古い本文として扱わない。
- EnterはキーリピートとShift+Enterを除外した送信候補として扱い、実際に同じ編集欄が空になった場合だけ送信確定にする。候補未確定時は監視を続け、後続候補が到着したら先行候補を失効させる。IME状態そのものはフック内でUI Automationを呼ばずに取得できないため直接判定せず、IME確定だけでは通常入力欄が空にならないことと後続送信候補で誤学習を防ぐ。
- アプリの音声送信は、SendInputのEnterより前にスナップショットを準備し、送信失敗時はその候補を破棄する。送信ボタン経路では、同じ監視対象欄の直近500ms以内の有効スナップショットだけを補助的に使い、同じ編集欄でのクリアを別途確認する。
- 対象ウィンドウとRuntimeIdで同じUIA編集欄を確認し、パスワード判定は維持。ValuePatternが失敗した時のみTextPatternへフォールバックし、メタデータ/プロバイダーの失敗段階と例外型を診断ログへ追加。監視ID・候補IDを候補抽出、モデル判定、辞書保存まで引き継ぎ、本文・用語・ウィンドウ名は記録しない。
- Release build/publish成功、0エラー。NU1900はNuGet脆弱性情報サービスへ接続できない警告。`dist/Better Flow.exe`と`bin/Release/publish-dictionary-repair/BetterFlow.exe`は82,440,542 bytes、SHA-256 `22D4733F48BA8014A9A67B11EBBB1B344DCF790CCE6DA19D9F6E91581B5A8452`で一致。配布前の旧版は`artifacts/BetterFlow-before-auto-dictionary-submit-fix-20261014-174536.exe`へ保存。置換時にパスが完全一致したBetter Flowプロセスを停止。
- Codex/Chromeでの実送信、IME実機、モデル判定、辞書保存通知は未確認。テストスイートは実行していない。次回は更新後EXEで訂正語を含む文章を送信し、診断ログで`submission_confirmed`→`candidates_extracted`→判定→`dictionary_saved`と設定一覧を確認する。

## 自動用語学習の精度向上と速度優先モード廃止（2026-10-14、実装済み）

> この節の「誤表記を保存しない」は当時の方針。現在は後続の「辞書に誤認識例を保存して再利用」により、正式表記と誤認識例を保存する。今回の追加修正は末尾の再レビュー計画を参照する。

- 要件: 複数語・スペース・製品バージョンを含む名前にも対応し、送信前の編集から再利用価値のある用語だけを自動登録する。正しい表記だけを保存する既存方針は維持し、誤表記・対応表・全文履歴は永続化しない。
- 実行証拠: 診断ログ17:51:54〜17:51:56 JSTの監視7cb59cbdは送信確認→候補1件→精度優先モデル承認1件→辞書保存まで成功。settings.jsonの辞書はParakeet 1件。次の17:52:42〜17:52:44、監視151df87dは送信確認→候補1件→モデル判定完了まで進み、承認件数0で保存に進まなかった。ユーザー報告の成功/失敗順と整合するが、本文をログに保存していないため例文との厳密な対応は推定。判定の空配列・confidence条件不足・出力項目不備を現ログは分けていない。
- 現設定は自動登録ON・精度優先・AI ON、OpenCode openai/gpt-6-luna-fast。この経路の候補抽出はスペースを禁止していない。空白を拒否するのは速度優先/ローカル代替側のIsLikelyTypoCorrectionで、今回の設定では直接原因ではない。
- 既存Services/DictionaryCorrectionCandidates.csをリンクした隔離コンソールで6ケースを追加モデル要求なしに確認。正しい複合表記Claude Fable 5から抽出されるafterはClaude Fable、5.5も同じくバージョンが外れる。共通数字トークンが差分の区切りに採用されるため。部分訂正ではFableだけになる。単一名Parakeetは全体を抽出。空白なしの複合表記も候補抽出されるが、かな→英字変換はローカル編集距離判定で落ちる。数値のみ100→200は速度優先側で200が採用され、意味変更の誤登録リスクを確認。
- 精度優先のモデル判定は変更片のbefore/afterだけを送り、周辺文脈・用語全体・見送り理由を扱わない。0.9以上というモデル自己申告値だけで採用するが、その値が実際の正答率90%を表す保証はない。未知の製品名/個人のプロジェクト名も許可するという指示が不足していることは改善対象だが、今回のモデル見送り理由は未確定。
- Typeless公式History & Dictionary（https://www.typeless.com/help/quickstart/history-and-dictionary）は音声入力後の訂正から好みの表記をAuto-addedへ登録し、人名・略称・専門語・語句を扱うと説明する。Personalization（https://www.typeless.com/help/quickstart/personalization）は書き方の一般的パターンを扱う別機能。今回確認した公開資料は、採用閾値・必要な訂正回数・候補抽出アルゴリズムを説明していない。以下はBetter Flow独自の提案であり、Typeless内部仕様の再現という主張はしない。

### 修正計画

1. **差分から用語全体を復元する。** 文字差分と位置情報を保持し、変更区間に隣接する名前の単語・空白・ハイフン・変更されていないバージョンを候補範囲へ含める。例ではClaude Fable 5を一つの候補にする。文末の依頼や助詞まで含めず、採用可能な範囲は修正後本文に実在する連続部分だけとする。数字が同じなら名前内の版として保ち、数字自体の変更は表記訂正と区別する。
2. **モデルへ短い文脈と変更種別を渡す。** 音声入力の修正前後から対象周辺の短い文を一時的に渡し、同じ名前のかな/英字表記・単語境界・大小文字・綴りの訂正と、意味/数値/日付/文体変更を分類する。ユーザー固有の未知の名前も表記訂正の対象とし、実在する有名製品であることを必須条件にしない。候補範囲からの選択と理由コードを返し、モデルが作った新しい表記や本文にない名前は採用しない。確信値を単に下げて承認を増やす修正は行わない。
3. **精度優先に一本化し、証拠に応じて自動登録する。** 速度優先モードの選択UI・分岐・ローカル登録処理を削除。旧設定値は起動時に読み捨てて設定から除去し、既存辞書は保持。AIオフ・モデル未準備時は自動学習を見送り、ローカル方式へ自動切替しない。モデルが同一用語の表記訂正と高確信で判定した候補は1回で登録。不確かな候補は保留し、異なる監視ID/送信IDで同じ正しい表記が3回確認されたら登録する。再送や二重信号は回数に数えない。数値や意味変更は候補として保留しない。
4. **正しい表記と必要なメタデータだけを保存する。** 保存対象は修正後の用語・手動/自動の区分・独立訂正回数・最終使用時刻など。誤表記、before/after対応、周囲の文章は保存しない。保留語は有効な用語辞書へ渡さず、承認済み自動登録は設定一覧へ反映して既存の保存通知を出す。削除された自動登録語は勝手にすぐ復活させない。
5. **今回の発話に関連する用語を優先して使う。** 現在の先頭100件の一括投入を、表記/読みの一致度と利用状況からローカルに順位付けした上位10〜20件程度へ変更する。読みの補助は正しい表記から一時的に導き、誤表記の辞書を作らない。低関連の名前で発話を置き換えず、バージョンを勝手に変えない。順位付けのために別のLLM要求を追加しない。Parakeetの重みやモデルは変更せず、LLM整形時の辞書適用を改善する。
6. **見送り理由と精度を確認できるようにする。** 未承認、確信条件不足、出力不備、文脈不足、意味変更、保留、反復不足を本文なしの理由コードで記録。複数語、かな/英字、数字保持、未知名、文体変更、数値変更、二重送信、削除後再登録、旧speed設定の移行、AIオフ時の学習見送りを検証する。局所処理はモデル要求なしのテストを使い、モデル実確認は少数候補を一括判定し既存の最大2要求/分・再試行なしを守る。登録件数だけでなく誤登録と再利用時の誤置換も評価する。

- 実装結果: 候補抽出で同じ数字の版を名前へ結合し、前後それぞれ最大320文字の一時文脈をモデルへ渡す。モデル応答はconfirm/pending/rejectと理由コードで検証し、元本文にあるafter表記だけを登録する。最大2モデル要求/分・再試行なしを維持。本文や誤表記はログ/設定へ保存しない。
- 保留は正しい候補表記・回数・時刻・重複防止IDだけを最大90日保存し、3回目で自動辞書へ移す。手動/自動の登録元、利用回数/最終利用時刻を保存し、削除した表記の再登録を抑止する。ユーザーの手動再登録は抑止を解除する。
- 整形時はカナ/ローマ字の音の近さと数字一致でローカル順位を付け、関連する上位15件だけを同じOpenCode要求へ渡す。使用履歴は順位の小さな補助に使い、別LLM要求は追加しない。Parakeet本体は変更しない。
- Git: 計画コミット `2d807af`、第1段階コミット `9445f32`、第2段階コミット `163b137`。各実装段階は直前のコミットをチェックポイントにして進めた。
- ビルド/配布: Release build成功、0エラー。NU1900はNuGet脆弱性情報サービスの接続警告。単体EXE `dist/Better Flow.exe` は82,451,743 bytes、SHA-256 `628FF014DB0123B24EBB5E0CF7645D9359D6FFA099CBDBBB13C3B2A0EAEBC624`。発行元と配布先のハッシュ一致を確認。旧版は `artifacts/BetterFlow-before-selective-learning-20261014-182907.exe` に保存し、完全パスが一致する旧EXEプロセスだけを終了して置換した。
- 未確認範囲: 実際のCodex/Chrome送信、モデルによる候補分類、3回目の自動登録、関連語の実整形結果は未確認。テストスイート/追加モデル要求は実行していない。

## 辞書に誤認識例を保存して再利用（2026-10-14、実装済み）

- 要件: 手動登録・自動登録とも、正しい表記と誤認識・別表記を一緒に保持する。誤認識例は参照用とし、同じ用語だと判断できる場合にだけ正しい表記へ直す。自動登録は常時有効とし、設定トグルと「登録した単語はAI整形時に自動で使います」の説明を削除する。
- 実装: 以前の設定形式`Alternatives`を復活し、既存の設定ファイルからも読み戻す。手動登録では複数の誤表記を区切り文字で入力できる。自動判定が確定した変更は誤表記をすぐ記録し、保留候補は独立した3回の確認後に誤表記と正式表記を一緒に有効辞書へ追加する。登録済みの誤認識例も辞書候補の関連度判定に使い、整形要求には正式表記と参照例を分けて渡す。
- UI: 登録フォームから既存一覧を外し、「単語を登録」の左隣へ同じ見た目の「辞書リスト」ボタンを追加。押すとウィンドウ内の一覧ページへ遷移し、左上の戻るボタンで設定へ戻る。
- 検証: Releaseビルド成功、0エラー。NuGet脆弱性フィードへの接続警告NU1900あり。UI実操作、実際のモデル補正、実音声入力による自動登録は未確認。テストスイートは実行していない。

## GPT-6 Astraの自動登録失敗を再レビュー（2026-10-14、実装済み）

### 証拠と前回説明の訂正

- 18:59〜19:01 JSTの監視3件は貼り付け検出・修正送信確認・候補抽出・モデル判定まで到達している。18:59:23は`confidence-below-pending-threshold`で棄却され、保留回数には数えられていない。19:00:34（03b4ed82）と19:01:16（60dfc1da）は各1候補が確定した後、`suppressed:1;added:0`で保存されていない。本文をログに残さないため、この3件とユーザーが述べた3試行の厳密な対応は未確定。
- 設定の唯一の抑止語は`GPT 6 Astra`。ソースは候補の正しい表記との大文字小文字を無視した完全一致で抑止する。したがって上記2件の抑止対象はこの表記と特定できる。`GPT-6 Astra`と`GPT 6 Astra`を自動的に同一キーへ正規化する実装ではない。ユーザーが望むハイフン付き表記が候補から失われたとは現ログだけでは断定できない。
- 削除した辞書項目の自動再登録抑止は以前の計画どおりで、3回の訂正で解除される仕組みではない。過去の抑止状態によってモデル承認後の保存が止まる点と、抑止状態をUIから確認・解除できない点が今回の改善対象。候補をすべて3回で登録する、モデルの確信閾値を下げる、抑止リストを全削除する対策は採らない。
- 19:02以降の監視は100/97文字、次は全文103文字中の挿入8文字を扱い、送信未確定や失効したUIA要素を記録している。これを上記3試行の原因とした前回説明を訂正する。後続の別入力やIME確定等のEnterも含む可能性があり、実送信の有無は未確認。ただし同じ失効要素を読み続け、後続Enterを無効候補として処理し続ける復旧不足はソースと19:08〜19:09のログから確認できる。

### 改訂した修正計画

1. **削除と再学習の扱いを明確にする。** 削除済み用語を勝手に復活させる現行抑止の保護は維持し、既存の「辞書リスト」ページから抑止された表記を確認し、該当語だけ「再学習を許可」できるようにする。解除は既存辞書や他の抑止語を変更せず、解除だけでは辞書へ追加しない。今回の`GPT 6 Astra`も、この明示操作後の新しい手修正・実送信から再判定する。アプリ全体の自動登録は常時有効のままとする。
2. **見送りを段階ごとに記録する。** モデル棄却、保留、削除済み抑止、重複、上限、保存失敗を別の完了理由とする。候補IDで送信→判定→保存を追跡し、抑止された候補をモデルの失敗と表示しない。保存通知は設定保存に成功した場合だけ表示する。本文・誤表記・用語は診断ログに残さない。
3. **UIA要素失効への復旧を別件として修正する。** 失効した要素・キャッシュ済みパターンを同じまま使い続けない。対象ウィンドウ内で要素とパターンを再取得し、元の編集欄との対応、非パスワードかつ編集可能であること、追跡範囲を検証する。RuntimeIdだけで再生成後の欄を同一と決めず、安定した識別情報と親の位置関係などで対応を証明できない場合は学習を見送る。失効やフォーカス移動を送信成功・空欄の証拠にしない。
4. **Enterの対象と送信確認を改善する。** 同じウィンドウで押されたEnterをすべて対象欄の送信と見なさず、対象欄のフォーカス状態と記録の鮮度をUIA処理側で確認する。フック内でUIAを呼ばない。IME確定・改行・未送信は学習しない。不変の送信前記録を保持し、実際の送信後クリアを同じ欄で確認する既存条件を維持する。待ち時間延長や古い本文の再利用を主対策にしない。
5. **指定表記と誤認識例を保持する。** 修正後本文に実在する表記を保存し、`GPT-6 Astra`のハイフン・空白・版を勝手に変更しない。誤認識例は同一用語を見つける参照情報として保存し、別の誤表記にも文脈に応じて対応する。確定候補は1回、妥当だが曖昧な保留候補だけ独立した3送信で採用し、棄却・抑止・未送信・重複は回数に数えない。
6. **解除・保存・入力欄復旧を分けて確認する。** モデル要求なしで抑止語の個別解除、他項目の保持、解除後再起動、確定候補の保存、棄却の非加算、要素失効、別欄Enter、IME確定、ハイフン保持を確認する。実操作ではCodex/Chromeの修正・実送信を監視IDごとに照合し、初めて辞書が増える段階と保存通知を確認する。モデル実確認は少数に限定し、最大2要求/分・自動再試行なしを維持する。実装直前にGitチェックポイントを作成し、更新EXEでは旧版を退避してから既存配布先を置き換える。

- 実装結果: 辞書リストに削除済み用語を表示し、該当語だけ再学習許可を保存できる操作を追加。許可だけでは辞書へ登録せず、新しい手修正と送信を待つ。UI Automation要素またはパターンが失効したら、フォーカス中の編集欄を再取得し、同一RuntimeIdを確認できる場合だけ監視を再接続する。照合できないときは自動登録監視を終了し、別欄や空欄を送信と誤認しない。Enterは直近に対象編集欄へフォーカスがあった場合だけ候補として扱い、古い/無効スナップショットは処理キューへ入れない。診断ログには本文や用語を保存しない。
- Git: 実装前の計画チェックポイント `473342d`。実装コミットIDは変更コミット完了後に追記する。
- Release build: 成功、0エラー。NU1900はNuGet脆弱性情報フィードへ接続できない警告。単体EXEは82,456,549 bytes、SHA-256 `35665AE0A548E7C200A10E1392672AA33B532D7E11F83CA67BCFECFDF29714B0`。`dist/Better Flow.exe`と発行物のハッシュ一致を確認し、旧版は`artifacts/BetterFlow-before-dictionary-autolearn-fix-20261014.exe`へバックアップ。
- 未確認範囲: 更新EXEを使った辞書リストの実操作、Codex/Chromeでの送信後登録、UIA失効からの再接続、キーボード・IMEの実操作。自動登録の追加モデル要求やテストスイートは実行していない。

## AI常時有効・マイク名表示・自動辞書登録・Chrome送信確認（2026-10-14）

- 要件: AI文章整形を常時有効にして設定画面から切替項目を削除。長いマイク名をドロップダウンで末尾まで表示。アプリ側の自動辞書判定回数制限を外す。Chrome版ChatGPTで送信後の欄変化を確認できず自動登録が見送られる問題を修正。
- 調査: 既存の送信判定は追跡中の文章範囲が空になるか、入力欄全体が空になることを要求する。2026-10-14の診断ログでは送信後に31文字から12文字へ変化した例と、送信後にRuntimeIdが変化して古いUIA要素が無効になった例を確認。本文はログに記録されておらず、12文字がChatGPTの`Ask anything`プレースホルダーであることは長さからの推定。Claude Desktopの空欄動作は従来条件で処理する。
- 実装: UI Automationが返す既知または入力欄のName/HelpText由来のプレースホルダーを、同じ欄の送信後クリアとして認識する。Webアプリが入力欄要素を再生成した場合、対象ウィンドウ・プロセス・編集欄型・AutomationId/class/framework・親要素・画面位置が安全に一致した場合だけ追跡を再接続する。送信確認では現在のフォーカス欄を再取得し、他の欄との一致が証明できない場合は引き続き登録を見送る。本文・プレースホルダー文字列・単語はログに保存しない。
- 自動辞書判定のアプリ側の分/回上限は削除し、複数の判定依頼は`SemaphoreSlim`で1件ずつ処理する。OpenCode側の実際のレート制限・エラー処理は維持する。
- Git: 実装をコミット済み。
- Release build/publish: 成功、0エラー。NU1900はNuGet脆弱性情報フィードへ接続できない警告。`dist/Better Flow.exe`は82,458,846 bytes、SHA-256 `F323B8E3371FA928D5F9702F217A094F1EA230C39526AF4A718E9CF5521A6B0A`で発行物と一致。更新前版は`artifacts/BetterFlow-before-chatgpt-dictionary-fix-20261014-200238.exe`へ退避。完全一致する配布先プロセスは実行されていなかった。
- 未確認範囲: Chrome版ChatGPTで送信・UIA再接続・辞書保存までの実利用は未確認。送信本文や追加モデル要求を使うテストは行っていない。

## ChatGPT系入力欄の再取得・送信確認失敗 — 調査、実装、追加修正（2026-10-14）

- 要件: 1回の自動学習成功後も「入力欄を安全に再確認できず、自動登録の監視を終了しました」が繰り返される。今回は原因調査と計画だけを行い、アプリ実装・EXE・実行設定を変更しない。
- ログ確認（20:07〜20:17 JST、`dictionary-learning-diagnostics.jsonl`）: 監視`683174f9`は20:10:47に同一RuntimeIdの欄でプレースホルダー復帰を確認し、モデル承認を経て20:10:49に1件保存。監視終了8件は`ElementNotAvailableException`の後に復旧失敗し、内訳は`focused-editor-not-found`4件、`editor-fingerprint-mismatch`4件。`same-editor-fingerprint`による再生成欄の復旧成功は0件。複数件は送信候補の作成前に監視を終了しており、学習モデルを呼ぶ前の障害と確認できる。送信未確認の他ログにはIME確定・改行等が含まれる可能性があり、全てを実送信の失敗とは扱わない。
- 実UIA確認（読み取り専用）: 現在のChatGPT文書の編集可能欄はControlType.Edit、TextPattern/編集可能ValuePatternあり、AutomationIdは空、ClassNameは`ProseMirror`、FrameworkIdは`Chrome`。親要素には一般的な`contents`と動的なCSSクラス、生成IDがある。本文・ラベル・URL・認証情報は出力/保存しない。公式ProseMirrorソース（https://raw.githubusercontent.com/ProseMirror/prosemirror-view/master/src/input.ts）はfocus/blurで`ProseMirror-focused`を追加/削除する。現コードのクラス名全体の完全一致はこの状態変化を同一性へ混入させる。各失敗でどの属性が不一致だったかは現ログにないため、4件全ての正確な不一致属性は未確定。
- ソース確認: 復旧はFocusedElementの祖先だけを検索し、元文書/編集欄コンテナー内から探す経路がない。読取失敗後に復旧できないと同じループで即returnし、未処理送信候補も失う可能性がある。初回の絶対座標・親のCSS全文を保持し続ける。UIAイベントの読み取りとeditor/TextPattern/ValuePatternの別々の参照差替えは並行しており、整合した取得単位と世代チェックがない。これらはコード上の不足だが、イベント競合が今回の各失敗を起こしたことは未証明。

### 実装承認後の修正順序

1. **失敗理由を属性単位で特定する。** ウィンドウ/文書/プロセス/型/安定クラス/親/位置/編集可能性/候補数の一致可否、欄の世代、フォーカス有無を理由コードとして記録する。本文・URL・ラベルや全CSS文字列はログへ保存しない。最初に既存条件のどこが失敗するか確認し、全条件を一律に緩めない。
2. **見た目の状態から独立した編集欄の識別を作る。** 対象ウィンドウ内のChatGPT文書と編集欄コンテナーを記憶し、元要素→フォーカス経路→元コンテナー→同じ文書内の限定検索で候補を取得する。`ProseMirror`等の安定したクラスのトークンと編集可能性を使い、focus等の可変クラス、クラスの順序、生成ID、初回の絶対座標を必須キーにしない。構造・同じ文書・本文の連続性・候補一意性を合わせて確認し、検索欄や別タブの欄へ追跡を移さない。位置は文書/ウィンドウに対する相対位置と直近の成功状態から補助判断する。
3. **一時的な失効を復旧状態として扱う。** Tracking/Reacquiring/Suspended/Submitted等の状態を区別し、最初のElementNotAvailableExceptionや一時的フォーカス移動だけで終了しない。構造変化・フォーカス復帰を契機に限定範囲で再取得し、別文書への移動や複数候補で識別不能なら学習を保留する。読取不能を空欄/送信成功と見なさず、古い本文の時刻を更新して新しい記録として流用しない。待ち時間延長だけを対策にしない。
4. **イベントと送信候補を整合させる。** UIAイベントは単一の取得処理へ通知し、要素・パターン・世代を一つの取得単位として差し替える。古い世代の通知を無視し、登録/解除を毎回の送信確認で繰り返さない。未処理の送信候補は復旧失敗で破棄せず、送信前の不変記録を使って同じ欄のリセットを確認する。設定画面の状態通知にも監視IDを付け、終了済み監視の通知が新しい監視へ表示されないようにする。
5. **実送信と編集の継続を区別する。** IME確定・Shift+Enter・未送信の訂正・別欄操作では登録しない。プレースホルダーは既知/明示的に確認した空欄表示だけを採用し、訂正後の本文を任意のName由来ラベルとして空欄扱いしない。新規チャットの初送信で文書/ルートが変わるケースは、保存済み送信候補と対象の最新送信文を限定的に照合できた場合だけ継続確認する。単なるタブ/会話切替や欄消去を送信と扱わない。
6. **入力欄復旧とモデル判定を分けて確認する。** モデル要求なしのイベント再現で、要素再生成、class変化、失効中の送信、フォーカスの往復、複数欄、タブ/会話変更、長文での欄拡大、IME/改行、古いイベントを確認する。Chrome ChatGPTで連続操作と初回送信を確認し、Claude Desktopの既存動作も確認する。実モデルの保存確認は少数の2〜3要求に絞り、回数上限を再導入しない。実装前にGitチェックポイントを確保し、確認後に既存の単体EXEを退避・更新する。

- 今回の範囲: 調査・計画文書のみ。現在のアプリ実装は`7bbe196`のまま、配布EXEのSHA-256は`F323B8E3371FA928D5F9702F217A094F1EA230C39526AF4A718E9CF5521A6B0A`のまま。ビルド・テスト・モデル要求・UI操作・送信は行っていない。

### 実装状況（2026-10-14）

- 実装: UIAイベントのコールバックは要素・パターンを直接読み取らず、世代付き通知を監視ループへ渡す方式に変更。入力欄、TextPattern/ValuePattern、RuntimeId、文書ID、世代を一つのbindingとして差し替え、古い監視の画面通知はMonitorIdで除外する。
- 再取得: フォーカス中の編集欄に加え、最初に記録したDocument要素の子孫から候補を探す。Document要素が取れない場合は文書RuntimeIdで対象を絞り、それも取れない場合はRuntimeIdが同じ入力欄だけを許可する。異なる文書、候補が曖昧な状態、再生成欄の本文に連続性がない状態では追跡を移さない。フォーカス状態を含む可変クラスは照合から外し、画面位置は補助点として扱う。
- 送信復旧: 一時的なUIA失効では最後に読めた本文を空欄扱いせず、Reacquiring/Suspended状態で監視を続ける。送信前のSubmitCandidateは不変スナップショットとして保持し、送信後の欄再生成・描画待ちを最大8秒間再確認する。入力欄が別要素に再生成された場合は、同じ文書に新規表示された送信本文との完全一致を確認してから登録する。
- プライバシー: 診断には拒否理由の分類、候補数、世代、読み取り結果、文字数などを記録する。入力本文、送信文、ラベル、URL、クラス全文、RuntimeIdはログに記録しない。送信前に画面上にあった読み取り専用UIA要素のテキストをSHA-256化し、送信後は新しい要素のダイジェストをメモリ内だけで比較する。さらに送信候補本文との正規化後完全一致を確認するが、本文・ダイジェストはログやファイルへ保存しない。登録候補そのものは元の送信前スナップショットからだけ取得する。
- ビルド: `dotnet build AutoPrompt.csproj --configuration Release --no-restore`成功、0エラー。続けてwin-x64自己完結単体EXEの発行も成功。NU1900はNuGet脆弱性情報フィードの接続警告。
- 実地確認: Chrome版ChatGPTとClaude Desktopでの連続送信・欄再生成を使った実操作は未確認。実モデル要求は実行していない。現時点で確認できたのはソース確認とReleaseビルドまで。
- 配布EXE: `dist/Better Flow.exe`へ発行済み。サイズ82,471,030 bytes、SHA-256 `609A5916DD25D6C7EC5E9EEC116DA6BA86080B43D55FEBC9F38806F202276E28`。発行元と配布先のSHA-256一致を確認。更新前版は`artifacts/BetterFlow-before-dictionary-rebind-20261014-214936.exe`へ退避。置換前に完全一致する配布先プロセスがないことを確認した。
- Git: 実装コミット後のIDはプロジェクト記録へ追記する。

### ChatGPT Desktop/Chromeの追加失敗に対する修正

- ユーザー報告: Claude Desktopでは自動学習が動く一方、ChatGPT DesktopとChrome版ChatGPTでは「送信後の入力欄の変化を確認できず」または入力欄再検索の表示で止まる。
- 追加原因: `ProcessSubmissionCandidateAsync`は入力欄の本文が変化し、追跡範囲が空になった後にだけ送信済み表示を照合していた。ChatGPT系でUIAが古い入力欄の値を返し続ける場合や再生成欄を再取得できない場合、送信済み本文の照合まで到達せず、辞書判定モデルを呼ぶ前に監視が終わる。
- 修正: 入力欄が読み取り不能・同一性不一致・本文不変・追跡範囲未消去のときも、送信前に保存した有効な候補に対応する新しい読み取り専用表示を、対象ウィンドウおよび元文書の範囲から探す。本文のNameとTextPatternを合わせて読み、空白・改行を正規化した完全一致、または12文字以上の候補に限る包含一致で照合する。本文のダイジェストだけを起動時に画面内メモリへ保持し、未送信候補や既存表示を新規送信と誤認しにくくする。文書スコープが取れる場合は元文書内を維持し、読み取り専用表示と有効な送信候補を必須にする。本文・ダイジェストはログやファイルへ保存しない。
- ビルド/配布: Release win-x64自己完結単体EXEの発行成功。NU1900はNuGet脆弱性情報フィードへの接続警告。発行物は82,471,986 bytes、SHA-256 `DA13D7B9DFF6065E29BF0FC84037A2ABB311FE723D9195E9BB6A0FB1CA933ACE`。実操作でのChatGPT Desktop/Chrome/Claudeの確認は未実施。

## Codexデスクトップで自動辞書学習の送信検知が止まる件（2026-10-14、計画・実装）

- ユーザー報告（分類: 実行環境での現象、出典: ユーザー報告、確認日: 2026-10-14、信頼度: 報告内容は高、内部イベントの原因は未確定）: Chrome版ChatGPT、Gemini、OpenCode Desktopでは自動学習が動作するが、Codexデスクトップでは「音声入力した文章を送信するまで監視しています」の状態で設定画面のログが止まり、送信後の学習へ進まない。
- ソース確認（分類: 送信検知経路、出典: `Services/AutoDictionaryLearningMonitor.cs`、確認日: 2026-10-14、信頼度: 高）: 監視開始後に送信候補を作る入口は、対象ウィンドウが前面にある時の非注入Returnキーと、UI Automationの`InvokePattern.InvokedEvent`を受け取った送信ボタンに限られる。Returnキー経路は追跡中の編集欄が直近1秒以内にフォーカスされていることも要求する。低レベルマウスクリックは監視していない。送信後の新規表示テキスト照合は、送信候補が既に作られた後にだけ呼ばれるため、送信シグナルが一度も届かない場合の救済にはならない。
- 診断ログ確認（分類: 実行時イベント、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`、確認日: 2026-10-14 22:39 JST、信頼度: 各記録は高、対象アプリとの対応は未確認）: 直近の監視例では`monitor_started`、`tracking_started`の後に`tracked_range_lost`が記録されたが、その監視IDに`submit_candidate_captured`、`submit_signal_ignored`、`submission_not_confirmed`は記録されていない。ログに対象プロセス名・ウィンドウIDを記録していないため、この例がCodexの操作かは断定できない。`tracked_range_lost`だけでは送信済みとも入力欄消去とも確定できない。
- 原因仮説（分類: コードとログからの推測、出典: 上記ソース・診断ログ・ユーザー報告、確認日: 2026-10-14、信頼度: 中）: Codexでの送信操作がReturnキー監視に届かない、送信時点で対象ウィンドウ/追跡欄のフォーカス条件を満たさず除外される、またはクリック送信がUI AutomationのInvokeイベントとして通知されない可能性がある。現行ログでは、キーを受信しなかった場合と条件で除外した場合、クリックがInvokeイベントを発生させなかった場合を区別できない。

### 実装承認後の修正順序

1. **本文を記録せず、送信シグナルの経路を観測可能にする。** 監視IDごとに、対象アプリの実行ファイル名、対象ウィンドウとの一致、キー種別と修飾キー、注入キーかどうか、追跡欄のフォーカス一致、送信ボタンのUIAイベント受信有無を記録する。ウィンドウタイトル、URL、入力本文、クリップボード、認証情報は記録しない。受信・除外・候補作成のどこで止まったかをログ上で区別する。
2. **Codexデスクトップで送信操作を分けて再現する。** 音声入力後の通常Enter、Codexが提示する送信ショートカット、画面上の送信ボタンのクリックを別々に確認し、Chrome版ChatGPT等の成功経路と比較する。対象アプリ識別をログへ追加してから監視IDを照合する。ここでは辞書判定モデルを呼ばず、検知段階だけを見る。
3. **観測した送信方法に対応する。** キーがフォーカス条件で除外されている場合はCodexの対象ウィンドウ/編集欄の同一性確認を修正する。送信ボタンのInvokeイベントが来ない場合は、低レベルマウスフックでは座標だけを監視ループへ渡し、UI Automation側で対象ウィンドウ内の有効な送信ボタンとの一致を確認して候補化する。Shift+Enterや別欄のキー入力を送信扱いしない。
4. **送信シグナルが取れない場合の限定的な補助判定を設ける。** 送信前に保持した有効スナップショットに対し、同一対象ウィンドウ内の入力欄が空/初期化され、かつ送信前には存在しなかった読み取り専用表示に送信文が一致して現れた場合だけ送信済みとする。単なる入力欄消去、画面切替、既存表示、部分一致だけでは候補を承認しない。送信後の証拠が取れない間は辞書判定へ進めない。
5. **誤検知を含めて確認する。** Codexでボタン送信・キー送信・連続送信を確認し、Shift+Enter、IME確定、未送信の全文消去、会話/タブ切替、別入力欄へのフォーカス移動では候補が生成されないことを確認する。Chrome ChatGPT、Gemini、OpenCode Desktop、Claude Desktopの既存動作も保つ。実モデル確認は、送信検知が通ると確認してから少数回に限る。

- 実装結果（分類: 送信検知・復旧、出典: `Services/AutoDictionaryLearningMonitor.cs`、Release publish、配布先SHA-256、確認日: 2026-10-14 Asia/Tokyo、信頼度: ソース・ビルド・配布一致は高、Codex実送信時の動作は未確認、情報区分: ローカル確認・未確認事項）: 監視開始ログに対象プロセス名とマウスフック状態を追加。Enterを注入有無・前面対象ウィンドウ・追跡欄の直近フォーカス・Shift/Ctrl/Alt状態とともに分類し、送信として採用/除外した理由を本文なしで記録する。低レベルマウスフックは対象ウィンドウの非注入クリック座標だけを監視ループへ送り、UI Automation側でクリック位置から有効な送信ボタンまたはInvoke可能なCustomコントロールを特定した場合に送信候補を作る。イベントが届かない場合は、欄の空化または追跡停止中に限り、8秒以内の新しい読み取り専用送信文表示を、対象ウィンドウ/文書スコープ・送信前ベースライン差分・送信文との正規化一致で確認する補助経路を追加。入力欄が空になっただけでは候補を確定せず、証拠がなければ学習へ進めない。診断ログに本文・ウィンドウ名・URL・用語を残さない。
- ビルド/配布: `dotnet publish AutoPrompt.csproj --configuration Release --no-restore --output artifacts/publish` 成功。コンパイルエラーなし。NU1900はNuGet脆弱性情報サービスへの接続警告。単体EXE `dist/Better Flow.exe` は82,474,468 bytes、SHA-256 `DDC1EE25CC2D5113DB1C08525BD386FEB88CB871005BFC8D0A1459E9241BD752`。発行元と配布先のハッシュ一致を確認。置換前の中間版を`artifacts/BetterFlow-intermediate-before-final-codex-submit-detection-20261014.exe`へ退避。
- 確認範囲: テストスイートと実Codex送信、辞書登録、他アプリ回帰のUI操作は未実施。実送信は外部サービスへの送信を伴うため行っていない。実機確認後に成功/失敗ログをアプリ別の監視IDと照合する必要がある。
- Git: 計画チェックポイント`cb54c33`を実装前に作成。実装コミットは本節のソース・配布記録とともに作成。

## Whisper Large V3 Turbo一時試用とParakeetへの切替（2026-10-15）

- 目的: Qwen3-ASRのメモリ負荷を避けるため、Whisper Large V3 Turboへ一時的に切り替えられるようにする。Whisperを既定にし、設定画面から既存のParakeet日本語モデルへ戻せる選択肢も維持する。
- モデル形式: Sherpa-ONNX公式モデル一覧が案内する`csukuangfj/sherpa-onnx-whisper-turbo`のINT8 encoder/decoderとtokenファイルを使う。公開一覧では同リポジトリ全体が4.27GBだが、INT8の必要ファイルはencoder約675MB、decoder約361MB、tokens約817KBで、合計約1.04GB。モデルファイルのSHA-256を検証し、Parakeetの保存場所やファイルは変更しない。
- 実行方式: 現在のSherpa-ONNX .NET Windowsランタイムを再利用し、Whisperは日本語文字起こし、INT8、CPU providerで動かす。現行Windows NuGetランタイムにはCUDA provider一式が含まれないため、この試用実装ではVRAMへ推論を移さない。VRAM利用は別途GPU対応ランタイムを配布する作業が必要。
- 設定と復元: `settings.json`に認識モデルIDを保存する。既定値をWhisperへ変更し、設定画面からParakeetを選び直せるようにする。モデルの切替は録音中には行わず、認識器を準備してから入れ替え、失敗時は現在の認識器を保つ。
- セットアップ: 選択中モデルに必要なものだけを取得し、WhisperはHTTPダウンロード中に一時ファイルへ書き、SHA-256を照合してから確定配置する。Parakeetの既存データは再利用し、不要な再ダウンロードを避ける。Whisperの必要空き容量表示は約1.3GBとする。
- 可逆性: この計画と実装のベースはParakeet版`03dd620`。Qwen試用ブランチ`codex/qwen3-asr-ja-trial`と実装コミット`909347b`も残す。さらにアプリ内のモデル選択でParakeetへ戻せる。
- 参照（公開一次資料、確認日: 2026-10-15）: [Sherpa-ONNX Whisperモデル一覧](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/whisper/export-onnx.html)、[Whisper Turboモデルファイル一覧](https://huggingface.co/csukuangfj/sherpa-onnx-whisper-turbo/tree/main)、[INT8 decoder SHA-256](https://huggingface.co/csukuangfj/sherpa-onnx-whisper-turbo/blob/main/turbo-decoder.int8.onnx)、[INT8 encoder SHA-256](https://huggingface.co/csukuangfj/sherpa-onnx-whisper-turbo/blob/main/turbo-encoder.int8.onnx)。
- 検証範囲: 計画チェックポイント時点では実装・ビルド・実音声認識をまだ行っていない。実装後はRelease単体EXEを発行し、ソースとEXEのハッシュを照合する。実モデル認識の精度・速度・実メモリ消費はユーザー端末での試用結果として扱う。
- 実装結果（分類: 音声認識モデル切替、出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/WhisperAsrSetupService.cs`、`Services/AsrService.cs`、`Services/Bootstrapper.cs`、Release publish、確認日: 2026-10-15 Asia/Tokyo、信頼度: 実装・ビルド・配布ファイル一致は高、実音声認識と端末上のメモリ消費は未確認、情報区分: ローカル確認・未確認事項）: 設定画面へ音声認識モデル選択を追加し、既定をWhisper Large V3 Turboにした。Parakeet 0.6B Japaneseへいつでも切替可能で、設定は再起動後も維持する。モデルのダウンロード先を分離し、WhisperのINT8 encoder/decoderはSHA-256確認後に配置する。切替時は録音中を拒否し、新しい認識器の準備に失敗した場合は現在の認識器を保つ。既存Parakeetのファイルや保存先は変更しない。
- 実行上の制約: Whisperは既存のSherpa-ONNX CPU providerで動作する。このビルドはVRAMを利用しない。INT8化はモデルファイルと必要メモリを抑えるが、実際のRAM消費量はユーザー端末での実行時に確認が必要。
- ビルド/配布: `dotnet build AutoPrompt.csproj --configuration Release --no-restore` と `dotnet publish AutoPrompt.csproj --configuration Release --no-restore --output artifacts/publish` は成功（0エラー、NU1900はNuGet脆弱性情報サービスへの接続警告）。単体EXE `dist/Better Flow.exe` は82,480,658 bytes、SHA-256 `B446D9710374FFB6B4DE4B55B2066DD1523C43D4B1457A571B2F22737EF63FE9`。発行物と配布先のSHA-256一致を確認。置換前のEXEは`artifacts/BetterFlow-before-whisper-turbo-final-20261015.exe`へ退避。
- 確認範囲: ソースの差分確認とReleaseビルド/発行は完了。テストスイートや実音声による認識精度・速度・RAM使用量は未確認。Whisperモデルを含む初回ダウンロードはアプリのセットアップ画面から実行する。
- Git: 実装前の計画チェックポイント`399623c`を作成済み。実装コミットは本節のソース・配布記録とともに作成する。

## Parakeetを音声認識の既定に戻す（2026-10-15）

- 目的: Whisper Large V3 Turboは速度面で試用に合わなかったため、Parakeet 0.6B日本語を既定に戻す。Whisperは設定画面から選べる追加オプションとして残す。
- 変更: 新規設定の初期値と、不明なモデルIDを補正するfallbackをParakeetにする。以前の版でWhisperが既定値として保存されていた設定は一度だけParakeetへ移行し、設定バージョンを記録する。移行後にユーザーが選ぶWhisper/Parakeetは再起動後も維持する。選択UI、モデル別ダウンロード/ロード処理、保存済みParakeetデータは変更しない。
- 確認: 差分確認とRelease build/publishを行い、単体EXEを更新して発行物とのSHA-256一致を確認する。実音声の速度・精度比較は行わない。
- Git: この計画を先にコミットし、その後に既定値のみを変更する。
- 実装結果（分類: 既定モデル変更、出典: `Services/AppSettings.cs`、`MainWindow.xaml.cs`、`README.md`、Release build/publish、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース・ビルド・配布一致は高、実音声の速度/精度は未確認、情報区分: ローカル確認・未確認事項）: 新規/無効な設定の既定をParakeet 0.6B日本語へ戻した。以前の版が既定として保存したWhisperは設定バージョンで初回だけParakeetへ移行し、移行後にユーザーが設定画面で選んだモデルは再起動後も維持する。Whisperは設定から選べる。`dotnet build`とRelease単体EXE発行は成功（0エラー、NU1900はNuGet脆弱性情報サービスへの接続警告）。`dist/Better Flow.exe`は82,480,741 bytes、SHA-256 `0C2860963CA521CF138F553523D435AB5B0B94122A6AD0C3288826A4B939CB58`で、発行物と一致。置換前Whisper版は`artifacts/BetterFlow-before-parakeet-default-20261015.exe`に退避。実音声比較は行っていない。

## 通常音声入力中の音声送信指示を修復（2026-10-15）

- 要件: 通常音声入力のAI整形中に「送信して」「送って」と話したら、整形結果を入力欄へ貼り付けた後にEnterを送る。選択文編集など別モードには広げない。
- 静的調査: `MainWindow.StopAndProcessAsync`には既にAI整形中の別マイク録音とローカル文字起こしがあり、検出後に貼り付け・Enter送信する経路もある。現状は認識例外を監視全体で握りつぶし、通知を残さない。`ContinuousMicrophoneRecorder`はSilero VADで8連続speech frameを満たす前に録音を止めると、pre-roll音声を発話として確定しない。このため短い指示や整形完了時にかかった指示を失う可能性がある。実機の発話ログは未確認で、今回の実際の失敗原因はまだ断定しない。
- 実装修正: 送信語尾の照合を音声認識の句読点・空白・「ください/下さい」に対応させる。短いコマンド用の連続録音だけVADの開始判定を8 frameから4 frameへ調整し、他の録音モードは従来設定を保つ。1発話のASR例外では監視ループ全体を終了せず、後続の送信指示を認識する。
- 実装結果（分類: 音声コマンド修正、出典: `Services/VoiceCommandParser.cs`、`Services/ContinuousMicrophoneRecorder.cs`、`MainWindow.xaml.cs`、Release build/publish、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース・コンパイル・配布一致は高、実マイク認識と外部アプリ送信は未確認、情報区分: ローカル確認・未確認事項）: 通常音声入力のAI整形中に動く送信指示監視を維持し、短い「送信して」「送って」が拾われやすいよう監視録音だけVAD開始閾値を下げた。送信指示の表記ゆれ・句読点・丁寧形を許容し、個々の音声認識失敗で後続監視を止めない。対象アプリが前面でない場合は送らない既存の安全確認を維持。Release build/publish成功（0エラー、NU1900はNuGet脆弱性情報サービスへの接続警告）。単体EXEは82,481,288 bytes、SHA-256 `8BB7192B8C512235844BBF94251BF16E7B6A50B85997F5E6ABBCFDC834EA1F12`。`dist/Better Flow.exe`とのハッシュ一致を確認。置換前版は`artifacts/BetterFlow-before-voice-send-20261015.exe`へ退避。実音声・実送信およびテストスイートは未実施。
- Git: 実装前計画コミット`1d3bd8c`。ソースと配布物の実装コミットを作成する。

## GPT Liveの注意表示・Parakeet専用化・ドロップダウンのスクロール修正（2026-10-15）

- 要件（分類: UI・音声認識・操作、出典: ユーザー指示、確認日: 2026-10-15、信頼度: 高、情報区分: 明示情報）: 「選択した文章への回答方法」の直下へGPT LiveにChatGPTサブスクリプションが必要な旨を小さく表示する。Whisperのアプリ内サポートを取り除き、Parakeet専用にする。文字起こしモデル選択設定を削除する。ドロップダウン上のホイール操作で選択モデルが変わらないようにする。
- 変更: 回答方法のドロップダウン直下に小さな注意書きを追加。初回セットアップの説明、README、モデル準備・準備済み判定・音声認識ロード経路をParakeetだけに統一し、モデル選択UI、Whisper固有のセットアップ/認識コードと保存先定義を除去した。旧版の設定ファイルに残る文字起こしモデル選択キーは、設定読み込み時に通常の設定保存を通じて除去する。既存のWhisperモデルデータはアプリから参照しないが、ユーザーデータとして自動削除はしない。閉じた状態の設定ドロップダウンでマウスホイールを受けた場合は選択変更を抑止し、設定画面のスクロールへ渡す。開いたリスト内の通常スクロールは維持する。
- 実装結果（分類: UI・Parakeet固定・ホイール操作、出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AppSettings.cs`、`Services/AppPaths.cs`、`Services/AsrService.cs`、`Services/Bootstrapper.cs`、`README.md`、Release build、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース・コンパイルは高、実機UI操作と音声認識は未確認、情報区分: ローカル確認・未確認事項）: GPT Liveサブスクリプション注意書きを追加。Whisper選択肢・セットアップ・ロード経路を削除し、音声認識をParakeetに固定。古い設定キーはアプリの次回起動時に設定から取り除く。閉じた設定ドロップダウン上のホイールで選択値が変わらないようにし、画面の縦スクロールに回す。既存のWhisperファイルを削除する処理は追加していない。
- 確認範囲: `dotnet build AutoPrompt.csproj --configuration Release --no-restore`成功（0エラー、NU1900警告1件。NuGet脆弱性情報サービスへの接続不可）。続いて`dotnet publish AutoPrompt.csproj --configuration Release --no-restore --output artifacts/publish`成功（0エラー、同じNU1900警告）。テストスイートおよび実機でのドロップダウン操作・音声認識確認は未実施。
- EXE反映: 起動中プロセスを確認したがBetter Flow/AutoPromptの該当プロセスはなかったため、停止対象はなかった。`artifacts/publish/BetterFlow.exe`で`dist/Better Flow.exe`を置き換え、サイズ82,475,634 bytes、SHA-256 `2B8D40B4B582688F1AB196C69CE87731552DC020AAB508ED016F20A179C26EF1`を確認。発行元と置換後のハッシュは一致。
- 継続する作業手順（分類: リリース作業の希望、出典: ユーザー指示、確認日: 2026-10-15、信頼度: 高、情報区分: 明示情報）: 今後このアプリの実装を行う際は、Release単体EXEの発行後、配布先と完全一致する実行パスのプロセスだけを確認して停止し、既存の`dist/Better Flow.exe`を置き換え、発行物と配置後のハッシュ一致まで確認する。
- Git: 変更前の作業ツリーはクリーンで、実装前の復元ポイントは`edbb8f6`。ソース実装コミットは`22c5128`。作業手順とEXE反映結果を記録する文書コミットを作成する。

## Codex/ChatGPT Desktopで自動辞書学習の送信検知が止まる問題（2026-10-15）

- ユーザー報告（分類: 実行時不具合、出典: ユーザー報告、確認日: 2026-10-15、信頼度: 高）: Chrome/Claude Desktopでは自動学習が動く一方、Codex/ChatGPT Desktopでは送信後も「音声入力した文章を送信するまで監視しています」が頻繁に残り、学習が始まらない。
- 診断ログ（分類: 実行時イベント、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`、確認日: 2026-10-15 16:16 JST、信頼度: 記録イベントは高、Codexとの対応付けは未確認）: 保存された347イベント、17監視IDの対象名はChatGPT 13、chrome 3、不明1で、`Codex`という対象名はない。ログ内に`submission_confirmed`はない。ChatGPTの1監視では対象ウィンドウ前面のEnterが24回`tracked-editor-focus-not-current`で棄却され、送信ボタンクリックも`no-button-at-point`となり、送信候補は作られなかった。別の監視では送信候補を取得したが、その後のEnterが同じフォーカス理由で棄却された。
- 入力欄再生成の証拠（分類: UI Automation実行時状態、出典: 同診断ログ、確認日: 2026-10-15 16:16 JST、信頼度: 高）: `ElementNotAvailableException`後の再取得で`no-matching-editor`/`content-continuity-mismatch`となり、スナップショットが`RangeLost`になったEnterは`snapshot-not-fresh:RangeLost`として無視された。送信候補を取得した別例でも、編集欄の再取得失敗と`no-new-exact-outgoing-text-node`により約179秒後に`submission_not_confirmed`となった。
- コード上の原因（分類: 制御フロー、出典: `Services/AutoDictionaryLearningMonitor.cs`、確認日: 2026-10-15、信頼度: 高）: Enter採用条件が前面ウィンドウに加えて直近1秒の追跡欄フォーカスを必須とし、リッチテキスト入力欄のフォーカス通知/同一性判定が外れると有効なEnterも候補化しない。クリック検出は`AutomationElement.FromPoint`から対象ウィンドウ内の送信名を持つボタンを辿れない場合に止まる。候補ができても、送信後のUIA要素再取得・欄の消去・新しい読み取り専用送信文という厳格な確認が必要。候補受付時に画面状態を更新しないので、確認中も開始時の「監視しています」が表示され続ける。Codex固有の実行ログはないためCodexでの正確な拒否理由は未確定。

### 実装計画（実施済み）

1. Enter信号の採用を、対象ウィンドウ前面・非注入・Shiftなし・鮮度のある有効スナップショットに基づけ、直近フォーカス検出だけで落とさない。送信登録の確定は現在の送信後証拠で引き続き検証し、Enterを押しただけでは学習させない。
2. 送信ボタンのUIA取得を、FromPointの親だけに依存しない対象ウィンドウ内の有効ボタン/Invoke可能な要素の位置照合で補う。送信らしい名称とクリック位置が一致する場合だけ候補化し、最終的な送信後確認は維持する。
3. 候補受付時に「送信後の変化を確認中」と状態を直ちに表示する。再取得・送信確認の失敗理由を既存の本文非記録ログへ反映し、失敗後に監視中表示だけが残らないようにする。
4. UIA要素が再生成されたときは、送信前の不変候補と対象ウィンドウ内の新しい読み取り専用表示を先に照合してから高コストの再bindを行う。対象範囲、送信前ベースライン、候補本文との照合で誤検出を防ぎ、読み取り欄消去だけでは登録しない。
5. `dotnet build`とソース/差分確認を行う。外部アプリへ実送信する自動テストやモデル要求は行わない。Release単体EXEを発行し、配布先と完全一致する起動プロセスだけを必要時停止して`dist/Better Flow.exe`を置換し、発行物とのハッシュ一致を確認する。
- Git: 実装前チェックポイント`1378ce2 docs: plan desktop dictionary send detection fix`を作成済み。今回のソース・配布記録はその後の実装としてコミットする。
- 実装結果（分類: 送信検知・確認、出典: `Services/AutoDictionaryLearningMonitor.cs`、Release build/publish、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース/ビルド/配置ハッシュ一致は高、Codex/ChatGPT実送信は未確認）: Enterは対象ウィンドウ前面・非注入・Shiftなしを条件に候補化し、直近フォーカス時刻だけを理由に除外しない。フォーカスイベントはPID一致を要求せず、対象ウィンドウ内で追跡中の編集欄またはその子孫に限ってフォーカス時刻を更新する。送信ボタンはFromPointの祖先照合に加え、対象ウィンドウのUIAツリーでクリック位置に重なる有効な送信名ボタン/Invoke要素を限定探索する。送信候補を受け付けたら確認中の状態をすぐ表示し、最新有効文面の`RangeLost`時にも送信後表示の補助確認を試す。対象ウィンドウ外・送信前から存在する表示・編集可能欄は証拠に使わず、送信前の読み取り専用要素ダイジェストが変わり、送信文との完全一致（元文書外は完全一致のみ）が確認できた場合に限って登録へ進む。
- 復旧上限: 送信候補ごとにUIA再bindは最大1回、送信後の読み取り専用表示照合は最大5回。各照合は対象範囲内の最大900要素を走査し、候補受付から8秒の確認枠を超えて再試行しない。無信号の補助候補にも最大5回の照合を適用。入力欄の消去だけ、または送信表示が見つからない場合は学習しない。
- ビルド/配布: `dotnet build AutoPrompt.csproj --configuration Release --no-restore`成功（0エラー、NU1900警告1件: NuGet脆弱性情報サービスへ接続不可）。`dotnet publish AutoPrompt.csproj --configuration Release --no-restore --output artifacts/publish`成功。最初の置換では配布先と完全一致したPID 53528だけを停止。最終置換時には同じ配布パスの起動プロセスがないことを再確認し、中間EXEを`artifacts/BetterFlow-before-codex-dictionary-send-fix-final-20261015-163531.exe`へ退避して置換。発行物と`dist/Better Flow.exe`は82,476,407 bytes、SHA-256 `95B4C8994230E55724A413A3BEE0E468CF161E9F13618B22FD6BCFA8CFE8F346`で一致。退避した中間EXEのSHA-256は`80683DAE79E9DE8BB6542CC94140E345D04537742A6201C79868857A3E87F306`。
- 確認範囲: ビルド・publish・差分整合性・発行物/配布先SHA-256を確認。テストスイートは実行していない。Codex Desktop、ChatGPT Desktop、Chrome、Claude Desktopで実際の送信や学習は未確認であり、今回は外部アプリへ実文を送信していない。

### ChatGPT Desktopで送信後の変化を確認できない追加報告（2026-10-15）

- 診断ログ（分類: 実行時イベント、出典: `%LOCALAPPDATA%/AutoPrompt/dictionary-learning-diagnostics.jsonl`、確認日: 2026-10-15 16:47 JST、信頼度: ログのイベントは高）: ChatGPT Desktopの監視IDではEnterキーを受信して有効な送信候補を作ったが、追跡中の入力欄は変化せず、新しい送信文の読み取り専用表示も見つからなかった。別の監視では入力欄の再取得に失敗した。該当ログには`send_button_click_detected`がなく、クリックは`button-name-not-recognized`または`window-scoped-send-button-not-found`として記録された。ログから「EnterがChatGPTの送信操作になった」とまでは確定できない。
- ソース確認（分類: 送信経路、出典: `Services/AutoDictionaryLearningMonitor.cs`、確認日: 2026-10-15、信頼度: 高）: Enterは送信ボタンかどうかを確認せず候補化する一方、マウス経路はUIAで「送信」等の名前を持つボタンと照合できた場合だけ候補化する。ChatGPT Desktopのアクセシビリティ名が見つからないクリックは除外される。候補後の表示確認では元の文書/ウィンドウ内の最大900要素を一つの合計上限で走査するため、先の文書走査が上限を使うとウィンドウ側の残り領域を確認できない。
- 原因の切り分け（分類: 診断ログとソースからの結論、出典: 上記2項目、確認日: 2026-10-15、信頼度: 中〜高）: 今回は辞書判定より前の送信確認段階で止まっている。主因はChatGPT Desktopの無名/異名の送信ボタンをクリック経路が候補化できないこと。Enter候補の失敗は、Enterが実送信でなかった場合と、UIAが送信後の欄/メッセージを読めなかった場合の両方が残り、現行ログだけでは区別できない。

#### 実装修正方針

1. UIA名を取得できない場合も、対象ウィンドウ内の追跡中エディターと同じ小さなコンポーザー領域に属し、クリック位置がその領域の右下側にある有効ボタンを、送信候補として限定的に扱う。単にEnterを押しただけでは登録しない。
2. この構造的なクリック候補は、入力欄のクリア/変化に加えて、新しく現れた送信本文の一致を必須にする。誤クリックでの自動登録を避ける。
3. 送信後のUIA走査では元文書と対象ウィンドウに別々の上限を設け、Document/TextPatternのルート自体も読み取り専用証拠として比較する。各監視の走査数・走査ルート数・未一致理由を本文なしでログに残し、同じ失敗が残る場合に次の原因を判別できるようにする。待ち時間は延ばさない。
4. 実装前チェックポイントを作成後、Release単体EXEを発行し、配布先と完全一致するプロセスだけを必要時停止して既存EXEを置き換え、SHA-256一致を確認する。外部アプリへの実文送信やテストスイートは実行しない。

#### 実装結果

- 変更（分類: ChatGPT Desktop送信確認、出典: `Services/AutoDictionaryLearningMonitor.cs`、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース/ビルド結果は高、実アプリ再テストは未確認）: 無名のボタンでも、追跡中の編集欄と同じ限定コンポーザー内にあり、右下側の有効ボタンとUIAで確認できたクリックだけを候補化する。構造から推定したクリックは入力欄のクリアに加え、新しく表示された送信文との一致を必須にする。Enter候補と別経路のクリックを重複扱いで捨てず、実際にボタンが押された場合はその候補で確認する。送信表示の走査では対象文書内の走査後に文書部分を除外したウィンドウ範囲も確認し、Documentルートの変化と走査数を本文なしで記録する。失敗時に現れる本文なしの理由ログを詳しくした。待ち時間上限は変更していない。
- ビルド/発行: Release buildとwin-x64単体EXE publishが成功（0エラー、NU1900はNuGet脆弱性情報サービスへ接続できなかった警告）。発行物と`dist/Better Flow.exe`は82,477,268 bytes、SHA-256 `A2CF7E5F3241627A2D70BE6EF5EF544BDB5EC9E1BCE68F8A2BCBD008D12DC62C`で一致。旧EXEは`artifacts/BetterFlow-before-chatgpt-desktop-dictionary-confirmation-20261015.exe`へ退避（82,476,407 bytes、SHA-256 `95B4C8994230E55724A413A3BEE0E468CF161E9F13618B22FD6BCFA8CFE8F346`）。置換前に、配布EXEと実行パスが完全一致するPID 6408だけを停止した。
- 確認範囲: テストスイートとChatGPT Desktopへの実送信は実行していない。コンパイル、発行、配布先とのSHA-256一致を確認済み。実装前チェックポイントは`81fa355`、ソース実装コミットは`b7206ff`。

## 音声入力中のDiscord自動ミュート（2026-10-15、実装済み・実機確認待ち）

- 目的: Better Flowがマイクを使っている間だけ、Discord Desktopへの音声送信を止める。Windowsのマイク全体をミュートするとBetter Flow自身の録音も止まるため、システム入力デバイスのミュートは使わない。
- 現状: `MainWindow.xaml.cs`は通常録音を`MicrophoneRecorder`、選択文編集・質問・送信音声コマンド監視を`ContinuousMicrophoneRecorder`で行う。GPT Live質問は`GptLiveQuestionSession`が独自に音声を使う。設定には既定マイク選択があり、`Services/AppSettings.cs`で保存する。
- 技術方針: Discord公式ヘルプにある「Push To Mute」のキーをBetter Flowから押下・保持し、マイク利用終了時に解放する。Discordの「Toggle Mute」は状態を反転させるため使わない。DiscordのUI AutomationやSocial SDKはミュート制御に使わない。WindowsのCore Audioで入力エンドポイントをミュートするとBetter Flow自身の録音も止まるため採用しない。実際のミュート状態および、事前に手動ミュートしていた場合の状態復元は実機で未確認。
- 入力方式の制約: DiscordのPush To MuteキーはユーザーがDiscord側で設定し、同じ専用キー組み合わせをBetter Flowにも登録する。Windows `SendInput`でキーを押したままにして解放するため、Discordが起動していないときはキーを送らない。キー送信に失敗した場合は、ミュートできたと仮定せず録音を開始しない。異なる権限レベル、Discord側のキー未設定、別アプリとのキー競合では期待どおり動かない可能性がある。設定は初期状態ではオフにし、Better Flow録音キーとの同一割り当てを拒否する。Discord側の手順を独立ページに表示する。実機確認前に「ミュート済み」とは表示しない。

### 実装計画

1. 実装前チェックポイント`de862c1`をGitへコミットした。実機確認は実施せず、Discordの状態を推測するUIA操作は行わない方針で進めた。
2. 音声設定にミュート機能の有効/無効チェック、専用キー入力、辞書リストと同じ戻るボタン付きのチュートリアルページを追加する。専用キーは設定ファイルに独立保存し、機能をオフにしても維持する。Better Flow録音キーとの同一割り当てを拒否する。
3. ミュート制御を独立サービスとして実装する。音声開始前にDiscord用キーを押下し、同じマイク利用セッションが続く間は保持する。停止時はキーと修飾キーを必ず解放し、開始失敗・例外・キャンセル・通常終了・アプリ終了の全経路で二重操作なく後始末する。複数の録音経路が切り替わるときは参照カウント付きのセッションで一時的な解除を防ぐ。
4. 通常音声入力、AI整形中の「送信して」監視、選択文編集、選択文質問、GPT Live質問に共通の音声セッションとして組み込む。Discord Desktopプロセスが無ければキーを送らず通常どおり録音する。Windowsのマイク全体には触れない。
5. 手動で確認する項目は、DiscordのVoice Activity中の開始/終了、手動ミュート状態、途中キャンセル、連続編集、AI処理中の音声コマンド、マイク開始失敗、アプリ終了。実際のDiscord接続、ミュート状態の復元、音声文字起こしは未確認。
6. Release単体EXEのビルド、配布先プロセスの確認・停止、既存EXEのバックアップと置き換え、SHA-256一致を確認する。

- 公開資料（2026-10-15確認）: [Discordのキー割り当て](https://support.discord.com/hc/en-us/articles/217083547-How-do-I-add-different-Keybinds)、[Discord Social SDKのVoiceSettings](https://discord.com/developers/docs/social-sdk/classdiscordpp_1_1VoiceSettings.html)、[Windows SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[Windowsの音声エンドポイントミュート](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-setmute)。

#### 実装結果

- 変更（分類: 設定・音声セッション連携、出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AppSettings.cs`、`Services/DiscordPushToMuteController.cs`、確認日: 2026-10-15 Asia/Tokyo、信頼度: ソース・ビルド・配布ハッシュは高、Discord実機動作は未確認）: オプトインのDiscordミュート設定と専用ショートカット割り当てを追加。Discord設定の手順を独立ページに表示し、左上の戻るボタンで設定へ戻る。ミュートのオン/オフとは別にショートカットを保存し、オン/オフを切り替えても保持。通常音声入力、選択文編集/質問、GPT Live、AI整形中の送信音声監視の各マイク利用区間でDiscordの「プッシュしてミュート」キーを押下保持し、終了・キャンセル・開始失敗・アプリ終了で解放する。Discord Desktopが動作していない場合はキーを送らず、キー送信エラー時は録音開始を止める。Windows全体のマイクミュートとDiscordのToggle Muteは使用しない。
- Release発行: `dotnet publish AutoPrompt.csproj --configuration Release --no-restore --output artifacts/publish-discord-auto-mute`が成功。NuGetの脆弱性情報を取得できず`NU1900`警告1件。発行物と`dist/Better Flow.exe`はいずれも82,481,669 bytes、SHA-256 `1A252A9E12B6C0C57B6E5B9660AF855D5E6B64782ACFCD98CDD66974FDC923C0`で一致。旧EXEは`artifacts/BetterFlow-before-discord-auto-mute-de862c1.exe`へ退避（82,477,268 bytes、SHA-256 `A2CF7E5F3241627A2D70BE6EF5EF544BDB5EC9E1BCE68F8A2BCBD008D12DC62C`）。配布EXEと完全一致するPID 39260を停止してから置換した。
- 確認範囲: Release publishと単体EXE、配布先とのSHA-256一致を確認。テストスイート、UI操作、Discordでの実ミュート、手動ミュート状態の復元は未確認。実装前チェックポイントは`de862c1`。

## Discord設定画面の微調整（2026-10-15）

- Discordミュートのキー設定に関する説明2件を設定画面から削除。オン/オフやキー保存時の状態表示からも、キーを無効化後に保持する案内と、Discordへ同じキーを設定する案内を除いた。
- 設定画面のスクロールバーを6px右へ移動し、本文領域幅とボタン配置を保った。辞書リストとDiscord設定方法ページの戻る行には、アイコン下に8pxの余白を追加した。
- 「選択した文章への回答方法」の2項目に既存のカスタムドロップダウン項目スタイルを指定し、選択中の明るい背景に濃い文字が表示されるようにした。
- Release publish成功。`NU1900`（NuGet脆弱性情報フィードへ接続できない警告）1件。発行物と`dist/Better Flow.exe`はいずれも82,481,558 bytes、SHA-256 `D28C038F784CE67B9478FFBDCF559AAB8D1694C94D1896633E85DE0358C47357`。変更前のEXEを`artifacts/BetterFlow-before-settings-ui-fixes-11bd8c9.exe`へ退避（82,481,669 bytes、SHA-256 `1A252A9E12B6C0C57B6E5B9660AF855D5E6B64782ACFCD98CDD66974FDC923C0`）。置換前に配布先と完全一致するBetter Flowプロセスだけを停止。
- 確認範囲: `git diff --check`、Release publish、発行物と配布先のSHA-256一致を確認。テストスイート、実画面の目視確認、Discord実機動作は未確認。

## 設定画面をシンプルにする計画（2026-10-15）

- 対象と確認範囲: Windows WPFの640×640設定ウィンドウ。Computer Useで設定画面の上部・中段・下部を再確認し、マイクと回答方法のドロップダウン、辞書リスト、Discord設定方法ページも目視した。アクセシビリティツリーで主画面の項目順を確認。セットアップ画面、別解像度/DPI、キーボードだけの操作、失敗状態の再現は未確認。
- 確認できた点: ショートカット、マイク、Discord自動ミュート、OpenCodeモデル、辞書入力、選択文への回答方法が一つの縦スクロールに続く。640×640の初期表示では、説明・ショートカット・マイク・Discord設定の一部までが見え、モデル以降はスクロールが必要。現行の暗色カード、丸角ボタン、ページの戻る動線はまとまっている。Discord設定方法は7ステップで、辞書ページも一覧項目と削除操作を読み分けられる。両方のページで戻るボタンとアイコンの間隔は十分。マイク名は選択表示と候補一覧の両方で途中までしか読めない。回答方法ドロップダウンは開閉でき、選択行は明るい背景に暗い文字で読める。選択中モデルは`DeepSeek V4.1 Flash Free`。辞書欄の下に「送信後の入力欄の変化を確認できず、登録を見送りました。」という状態文が残っている。これは表示中の事実だが、継続中のエラーか前回操作の結果かは今回の画面だけでは確定できない。
- 計画:
  1. 設定を「基本」と「その他」の2ページに整理する。「基本」にはショートカット、音声入力デバイス、OpenCodeモデルを置き、主要3項目を640×640でスクロールせず確認できる構成にする。「その他」にはDiscord自動ミュート、用語辞書、選択文への回答方法/GPT Liveをまとめる。辞書一覧ページとDiscord手順ページの戻る動線は維持する。
  2. マイク名は選択済み表示と候補一覧のどちらでも末尾まで確認できるようにする。候補行の折り返し、詳細表示、十分な幅のいずれかを使い、ツールチップは補助にする。
  3. 辞書などの操作結果は、成功・失敗した直後だけ短い状態表示またはインジケーター形式の通知を出す。古い状態を設定画面に残し続けず、失敗時には次に取る操作を短く示す。
  4. 現在読みやすい回答方法ドロップダウンの配色、暗色テーマ、丸角、ボタン形状は保つ。ページを増やすだけの装飾や新しい設定用語は足さない。
  5. 実装後は640×640で「基本」の主要3項目が見えること、キーボードだけでページ移動・設定・戻る操作ができること、マイク名が両方の表示で切れないこと、100%/200%表示でボタンやラベルが重ならないことを確認する。
- 実装状態: 計画のみ。今回も設定画面とアプリの動作は変更していない。
- 情報の区分: Computer Useで確認した表示とアクセシビリティツリー、以前のXAML静的確認を区別した。DPI、キーボード操作、エラー文の継続条件は未確認。


## Better Flow — 直前文脈を使うParakeet文字起こし補助計画（2026-10-15、未実装）

### 目的
通常の音声入力で、ショートカット起動時にフォーカスされている入力欄の直前の文章を短く取得し、固有名詞や直前の話題をParakeetが誤りにくくする。対象は通常音声入力に限定し、選択文編集・文章質問・GPT Liveの録音には影響させない。Parakeetの既存モデル、配布サイズ、VRAM/RAM使用量、最終認識経路を維持することを優先する。

### Amicalから参考にする点と相違
- Amicalの公開コード・報告では、対象アプリのキャレット直前テキスト、前の文字起こし、語彙をWhisperの `initial_prompt` にまとめる設計が示されている。[Amical issue #170](https://github.com/amicalhq/amical/issues/170)
- ただしBetter FlowはWhisperではなく、Sherpa-ONNXのNeMo CTCモデルを `greedy_search` で実行する。Whisperの `initial_prompt` を受け取る同等の入口はなく、Sherpa-ONNXのhotwords/contextual biasingもtransducerモデルと `modified_beam_search` 限定である。現在のCTC経路へテキストをそのまま渡せるとは見なさない。[Sherpa-ONNX hotwords仕様](https://k2-fsa.github.io/sherpa/onnx/hotwords/index.html)
- Amicalの報告には、入力欄のプレースホルダーや別言語のUI文字列が認識言語を引っ張る問題も記録されている。したがって、アプリ表示文字、プレースホルダー、キャレットより後ろの文、別入力欄の文章を文脈に含めない。

### 実施順
1. **文脈取得の安全条件を定める。** ショートカット起動時に、同じ前面アプリ・同じフォーカス中の編集欄からキャレットより前のユーザー入力だけをUI Automationで読む。最後の1〜2文を上限付きで使い、全文や他欄は取得しない。選択範囲がある場合、通常音声入力では対象範囲と文脈の境界を明示し、識別できなければ文脈なしで続行する。取得文はセッション中のメモリだけに置き、ログ・設定・学習データへ保存しない。
2. **Parakeet CTCへの直接バイアス付けを先に実現可能性調査する。** 現在のSherpa-ONNX .NET APIがCTC出力を保ったままprefix/phrase biasingを追加できるか、Parakeetの既存ONNX・語彙・トークナイザーで実装できるかを調べる。可能なら追加モデルを使わない局所的な候補スコア調整を優先し、現在のgreedy経路への影響、CPU負荷、配布ライセンスを確認する。APIにない機能を設定値だけで実装できるとは扱わない。
3. **直接方式が難しい場合の代替を分けて判断する。** 代替案は、現在の認識結果に対して直前文脈中の候補語だけを保守的に照合するローカル補正、またはAIモードで既存の整形要求に短い文脈を加える方法。後者はOpenCodeへ文章が送信され、Parakeetの生認識自体は変わらない。追加のLLM呼び出しは作らない。文脈の外部送信を許可なく有効にせず、どちらの代替を採るかは直接方式の調査結果とプライバシー・速度の比較後に決める。
4. **試験可能な設定にする。** 最初は設定からオン・オフでき、オフなら現行のParakeet経路へ完全に戻るようにする。初期値は実測結果を見て決め、予期しない文脈混入が見つかった段階では既定オンにしない。
5. **同じ音声を使った比較で効果を測る。** 固有名詞を含む発話、文脈と無関係な発話、空欄・プレースホルダー・別言語UI、長文と連続利用を「文脈なし／あり」で比較する。固有名詞の正解率だけでなく、無関係な語へ誤って引っ張られる率、CER、ASR処理時間を記録する。基準音声と正解テキストはアプリログから収集せず、利用者が明示的に選んだ試験データのみ使う。
6. **採用条件を確認する。** 文脈なしの既存結果を不必要に変えず、誤補正が増えず、速度・メモリ・配布要件を許容できる結果が出た方式だけを実装する。効果が確認できない場合はParakeetや既定設定を変更せず、現行経路を維持する。

### 状態と情報区分
2026-10-15時点で計画のみ。アプリコード、設定、モデル、EXEは変更していない。Better Flowの現行CTC/greedy経路はローカルソースから確認。Amicalの方式とプレースホルダー由来の問題は公開Issue、Sherpaのhotwords制約は公式ドキュメントで確認。直接CTCバイアス付けの実装可否と性能への影響は未調査。


### 試験版の実装（2026-10-15）

- 実装前チェックポイント: `446363d chore: checkpoint before contextual Parakeet feature`。
- Sherpa-ONNXの現行CTC/greedy経路に文脈を直接渡すAPIがないため、この試験版ではParakeetのデコーダーは変更せず、フォーカス中の同じ編集欄から取得したカーソル前の短い文章を、既存のOpenCode整形リクエストへ参考情報として一緒に送る。追加のLLM呼び出し、別モデル、モデルダウンロードはない。これはParakeet自体を文脈条件付きで認識させる実装とは異なる。
- 設定に「直前の文章をAI整形の参考にする（テスト）」を追加。初期値はオフで、切り替えは設定へ保存。オフまたは文脈を安全に読めなかった場合は、従来のプロンプトと音声入力経路を維持する。
- 取得範囲は同じ前面ウィンドウの編集可能な入力欄、キャレット直前の最大280文字・直近2文。パスワード欄、選択範囲、空欄、placeholder相当、別ウィンドウの内容は対象外。文章は実行中だけメモリで保持し、ログ・設定には保存しない。通常音声入力だけに適用し、選択文編集と文章質問には適用しない。
- OpenCodeには文脈を用語候補の照合に限定するよう指示し、文脈にしかない語や指示を出力へ加えない制約を追加。文脈がない場合は旧プロンプトに戻る。
- Release buildは成功（0エラー、NU1900はNuGet脆弱性フィード接続警告）。単体EXEのpublishも成功し、発行元と`dist/Better Flow.exe`は各82,482,955 bytes、SHA-256 `D6A05B15FCA63F2CB5476C9A024AEA22057229CAE2A12E08E492131D476BFE7A`で一致。更新前EXEは`artifacts/BetterFlow-before-preceding-text-context-20261015.exe`に保存済み（82,481,651 bytes、SHA-256 `EA90A44CA57EAFE16D3993517FA3E594E0C29ECB9F77E7FD1FA17C288232A9C8`）。置換時に配布先と完全一致する実行プロセスは見つからず、停止対象はなかった。実マイク・対象アプリUI Automation・有効時の固有名詞精度と所要時間は未確認。テストスイートは実行していない。
