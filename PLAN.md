# Better Flow 実装計画・記録

## 目的

入力欄にフォーカスしてショートカットを押し、話した内容をローカルで文字起こしして元の入力欄へ貼り付ける。編集可能な入力欄で文章が選択されている場合は音声指示で文章を編集し、同じセッション中に続けて編集できる。通常テキストが選択されている場合はその文章について質問し、原文を変更せずに回答を表示する。新規設定ではAIモードをオンにし、Muse Spark 1.3 Contributor Freeで話者の言い方や語調を保ち、フィラーと自己訂正を整える。モデル選択は設定へ保存し、再起動後も維持する。言い直しでは変更箇所だけを置き換え、計画など変わらない依頼も残す。箇条書きは発話で複数項目が明確に列挙されたときだけ使う。初回セットアップは画面上の一つの操作で日本語音声モデルを取得して使えるようにする。

## 決定事項

- 対象はWindows x64。配布物は.NET 8を同梱した自己完結型の単一EXE。
- セットアップ画面は添付参考画像に合わせた濃い角丸パネル、左上のアイコン、右下の明るい `Setup` ボタンで構成する。標準タイトルバーを使わない透過フレームレスウィンドウにし、パネルを画面いっぱいに広げる。上部をドラッグ領域、右上にカスタムの閉じるボタンを置く。`Setup` の文字は無効時も黒で表示する。
- 画面・ウィンドウ・タスクバー・EXEのアイコンはプロジェクト直下の `icon.svg` を元に生成する。
- 音声認識はNVIDIA `parakeet-tdt_ctc-0.6b-ja` のINT8 ONNXモデル。初回にGitHubからアーカイブを取得し、サイズとSHA-256を確認してユーザーのローカルアプリデータ領域へ展開する。
- 文章変換はOpenCode経由に限定する。新規設定の既定モデルは `Muse Spark 1.3 Contributor Free`（provider ID `opencode`、model ID `muse-spark-1.3-contributor-free`）。設定画面の一覧はOpenCode serverのprovider/model一覧から取得し、OpenCode Desktopでプロファイルへ追加したモデルも表示・選択できる。選択したprovider/modelは設定ファイルへ保存し、再起動後も維持する。Better Flow内に直接API接続、APIキー、Base URLの設定を置かない。
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
