# Better Flow 実装計画・記録

## 目的

入力欄にフォーカスしてショートカットを押し、話した内容をローカルで文字起こしして元の入力欄へ貼り付ける。テキストが選択されている場合は、音声指示で選択文を編集し、同じセッション中に続けて編集できる。新規設定ではAIモードをオンにし、Muse Spark 1.3 Contributor Freeで話者の言い方や語調を保ち、フィラーと自己訂正を整える。モデル選択は設定へ保存し、再起動後も維持する。言い直しでは変更箇所だけを置き換え、計画など変わらない依頼も残す。箇条書きは発話で複数項目が明確に列挙されたときだけ使う。初回セットアップは画面上の一つの操作で日本語音声モデルを取得して使えるようにする。

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
