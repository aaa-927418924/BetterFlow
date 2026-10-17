# 技術的な学び

## Space音声コマンドとASR誤認識（2026-10-16）

- Space長押し中の録音区間は、通常の文字起こしではなく文章操作コマンドとして扱う。ASRが漢字の同音語や近い音へ誤変換しても、その発話自体がPTT中に行われたという情報はコマンド意図の判断材料になる。
- コマンド文は最終出力本文から除かれるため、送信後の文章修正を監視する辞書自動学習ではコマンドの誤認識を学べない。固定の誤認識置換表を増やすより、既存のOpenCode整形要求で漢字の読み・音の近さ・marker位置・本文文脈からコマンドを解釈する方針を使う。
- 確定した既知コマンドは低遅延のローカル判定を維持する。未知・誤認識のコマンドは追加リクエストを作らず既存の整形要求へ渡す。実マイクと選択モデルでの挙動は、ビルドや静的確認とは分けて確認する。
- LLMに音声コマンドmarkerを消させるだけでは、markerを削除して操作を黙って省略した応答を成功と誤認することがある。コマンドごとのID付き「適用済み/操作済み/未解決」markerを要求し、各IDがちょうど1つの結果markerに解決したか検証してから文章を貼り付ける。
- LLMが改行コマンドmarkerと通常の改行を同じ位置に両方出力すると、markerを改行へ復元した際に二重改行になる。改行marker周辺の通常空白・改行は一つの操作結果へ正規化しつつ、markerが連続する場合はmarkerごとの個数を維持する。本文由来の改行markerは別形式で保持し、コマンド改行の正規化対象に含めない。

## 音声入力の出力失敗と原文保全（2026-10-16）

- AI整形失敗時に認識済み原文を保持しながら出力処理を呼ばずreturnすると、入力欄もクリップボードも空のままになる。未出力の原文へ切り替えて貼り付け／コピーし、AI失敗後の自動送信は止めることで、処理失敗と送信を分離できる。
- 複数区間の音声認識は、各区間が確定した時点で原文をメモリに保持すると、後続区間の例外時にも途中までの文字列を回収できる。音声や文字起こしを診断ログに残さず、段階・例外型・HRESULT・原文の有無と文字数のみを記録して原因を追える。
- WPF Clipboard.SetTextの一時的なclipboard-busy HRESULT（0x800401D0）は限定的な再試行対象にできる。無制限に待たず、数回の短い遅延後に失敗を記録・表示する。

## PTT音声コマンドの雑音除外と本文分離（2026-10-16）

- Space長押しのPTT区間は通常の音声本文と分かれているが、ASRへ渡す前にSilero VADを通していないと、短い雑音もコマンド文字列としてAI編集へ流れる。PTTだけをVADで確認し、通常ディクテーションのVAD/停止動作は変えない。
- 未知PTT指示は本文内の位置markerと別の命令フィールドに保持する。プロンプトでは認識文を本文ではなく命令として扱い、marker以前の文章を対象に適用することを明示する。返答に指示の認識文がそのまま含まれた場合は、黙って成功扱いせず漏出を検出する。
- ASR文字列や音声は診断ログへ書かず、発話有無・区間長・処理段階などの最小メタデータだけを記録する。
- 実装・静的確認（分類: 音声コマンド品質、出典: `Services/GlobalHotkey.cs`、`Services/SileroVoiceActivityDetector.cs`、`Services/VoiceCommandParser.cs`、`MainWindow.xaml.cs`、確認日: 2026-10-16 Asia/Tokyo、信頼度: ソースとコンパイルは高、実マイクでの効果は未確認、情報区分: ローカル確認・未確認事項）: PTT区間境界はUIメッセージ待ちをせずキーボードフックから録音器へ同期通知できる。PTTだけにSilero VADを適用し、5有音フレーム（約160 ms）かつ3連続フレームを条件にすれば、通常ディクテーションの手動停止動作を変えず短い無音/雑音区間をASRから除外できる。AI指示のecho検査は元の本文に同じ文字列があるとき除外処理を行わず、実際の発話内容を削らないようにする。

## 通常音声の無音除外と整形コマンド認識（2026-10-16）

- 通常音声入力でも、録音停止を手動のまま維持しつつ、ASRの前段にSilero VADを「その区間に持続した発話が含まれるか」のゲートとして使える。プレビューと確定処理の両方で同じ判定を行うと、無音/非音声区間の一時的な誤認識が表示・出力に残るのを抑えられる。これは単語単位の認識ミス訂正ではなく、非音声区間をASRへ送らない対策である。
- 音声コマンドが完全一致のローカル判定を使う場合、同じ意味の自然な言い方を既知コマンドへ対応付けると、命令を本文から分離し、専用処理へ安定して振り分けられる。未対応の言い回しが汎用編集指示へ落ちると、生成モデルが命令文を本文へ混ぜたり、別位置へ動かす余地がある。
- 実装・検証（分類: ASR前段フィルターと音声コマンド、出典: `Services/VoiceCommandParser.cs`、`MainWindow.xaml.cs`、`Services/SileroVoiceActivityDetector.cs`、Release build/publishと配置ファイルSHA-256、確認日: 2026-10-16 Asia/Tokyo、信頼度: ソース・コンパイル・配置一致は高、実マイクとモデルの効果は未確認、情報区分: ローカル確認・未確認事項）: 「プロンプトに整形して」「プロンプトとして整形して」を専用コマンドへ追加。通常音声のプレビュー・確定ASR前にもVADを適用し、有音条件を満たさない区間を除外。単体EXEと`dist/Better Flow.exe`は82,513,116 bytes、SHA-256 `579D5F352B43E13DDDA574D1338E70B5231B17B71C6122346FE8E0707855B561`で一致。無音を含む実マイクでの再現・改善確認は未実施。
- 後続ユーザー確認・訂正（分類: 実動作報告と追加調査、出典: ユーザー報告、`OpenCodeService.cs:125`、`TranscriptRewritePrompt.cs:35`、`MainWindow.xaml.cs:3027`・`3100`、確認日: 2026-10-16 Asia/Tokyo、信頼度: ユーザー報告・静的条件は高、個々の最終ASR結果は不明、情報区分: 明示報告・ソース確認）: 無発話時の「あっ」は解消したと報告されたが、プロンプト整形と命令文漏出は未解決。PTT指示全体はプレビューで黄色だった。前の同義句追加だけで安定動作するとは扱わない。

## PTT編集指示の優先順位と実行検証（2026-10-16）

- 既知のプロンプト整形だけ専用APIへ分ける一方、未知PTT指示に通常の整形プロンプトを使うと、意味が同じでもASR表記や追加条件だけで別の方針が選ばれる。原指示や書式条件をboolへ縮約せず、AI編集の経路と本文/命令の分離方法を統一する必要がある。
- `RewriteAsync`の指示優先フラグが`voiceEditInstructions`の有無だけを見ていると、`voiceEditCommands`だけの要求では「明示編集は通常の文体維持・箇条書き抑制より優先する」という規則が付かない。プロンプトを組み立てる条件と、実際に命令データを渡す条件を揃える必要がある。
- コマンドIDのAPPLIED markerやHTTP/生成成功は、編集内容が反映された証拠とは限らない。指示の復唱を削除しただけで成功扱いすると、ほぼ原文のままの出力を隠してしまう。漏出と明示書式の検証を別に行い、任意の意味変換すべてを機械的に保証したとは扱わない。
- 検証根拠（分類: PTT編集の静的調査、出典: `VoiceCommandParser.cs`・`OpenCodeService.cs`・`TranscriptRewritePrompt.cs`・`PromptStyleRewritePrompt.cs`・`MainWindow.xaml.cs`・2026-10-16 10:20:01 JSTの`voice-command-output / removed-echoed-instruction`、確認日: 2026-10-16 Asia/Tokyo、信頼度: ソースとイベント記録は高、具体的発話との対応は不明、情報区分: ローカル確認・未確認事項）: 調査時点ではASR結果や要求経路を記録しておらず、どの発話がどの分岐に入ったかを断定できない。診断追加は本文・指示文・音声を保存せず、相関ID、分類、経路、文字数、前後認識の一致有無だけにする計画。実装はしていない。

## PTT編集コマンドの統合経路・書式検証（2026-10-16）

- 既知の「プロンプトに整形して」だけ専用APIへ分けると、その命令を一般の音声編集指示と違う契約で処理し、命令本文や明示書式を落とすことがある。既知・未知のPTT編集指示を一つの整形要求へ入れ、元の認識文・本文内位置marker・書式モードを別フィールドで保持する。
- `voice_command_segments`がある要求にも明示編集の優先規則を適用する。PTT命令は、話者の本文ではなく操作指示として、marker以前の本文へ適用する。プロンプト形式の要求は依頼内容・条件を保持し、依頼そのものへの回答や作業は行わない。
- 命令の復唱を除去しただけでは実行成功の証拠にならない。復唱検出時は失敗にし、失敗時は本文を保全して自動送信を止める。箇条書きのように機械判定できる明示書式は、追加LLM要求をせず出力行を検証できる。一方、意味の正しさ全体を機械検証できるとは扱わない。
- 診断の相関はランダムな処理IDとPTT区間番号で行い、プレビュー/最終認識の分類経路、結果、文字数だけを記録する。ASR本文・命令文・音声はログへ残さない。プレビューの色表示は確定処理のASR経路を証明しないため、最終経路も独立記録する。
- 実装・配布の確認（分類: PTT編集コマンド、出典: `MainWindow.xaml.cs`、`Services/VoiceCommandParser.cs`、`Services/TranscriptRewritePrompt.cs`、Release build/publish、確認日: 2026-10-16 Asia/Tokyo、信頼度: ソース・コンパイル・配置EXE一致は高、実マイクと実モデル効果は未確認）: Release buildは0エラー、NU1900警告1件。配置EXEと発行物は82,515,126 bytes、SHA-256 `AD266BF52A2DC2E9E82C164F20E9B24055E94FA111D7AFD6A63EEF29407AD31F`で一致。テストスイートは未実行。

## Amical Whisper認識パイプラインの参考調査（2026-10-16）

- 内容: Amicalの公開コードはWhisper Large v3 Turboそのものに加え、Silero VADで音声区間を抽出し、`suppress_blank` / `suppress_non_speech_tokens`を有効にして、直前の文字起こしや入力欄文脈をWhisper `initial_prompt`へ渡す構成。`whisper-prompt.ts`は文脈を短く整形しWhisperのprompt予算へ収める。Amical READMEはWhisper音声認識とOpen Source LLMによる知的処理の両方を明記するため、完成出力の精度をWhisper単体の精度と同一視しない。
- Better Flowへの応用候補: 現行のSherpa NeMo CTC INT8は`greedy_search`/CPUで、通常入力ではSilero VADの持続発話ゲート後に録音全体をASRへ渡している。優先候補はVADで発話区間を保守的に切り出し、前後余白と短い無音を保って無音・雑音だけを削ること。次点はSherpaのCTC beam/FST・一般日本語言語モデルで候補系列を再評価すること（正確なParakeetチェックポイントと配布設定で互換性・遅延を測る必要あり）。Whisper型のprompt文脈は現行Parakeet CTCへそのまま適用できない。
- 注意: Amicalの公開Issue #170は、入力欄のplaceholder/異言語テキストを`initial_prompt`に混ぜると、固定言語設定を覆して認識言語まで誘導し得る例を記録している。将来文脈promptを試す場合はUIヒントを除外し、言語一致・長さ制限・prompt有無のA/Bを行う。VAD区間切り出しは子音を欠かさない余白と区間結合が必要で、無音誤認識や処理時間には効いても同音語の漢字選択を直接解決するとは限らない。
- 出典・確認日・信頼度: Amical `apps/desktop/src/pipeline/providers/transcription/whisper-provider.ts`、`whisper-prompt.ts`、公開Issue #170、Sherpa-ONNX `OfflineRecognizerConfig`/offline CTC docs、ローカル `Services/AsrService.cs` と `MainWindow.xaml.cs`。確認日: 2026-10-16 Asia/Tokyo。ソース構成の確認は高、精度効果・速度は未測定。情報区分: 外部公開ソースとローカルソース確認。
- 参照: https://github.com/amicalhq/amical/blob/main/apps/desktop/src/pipeline/providers/transcription/whisper-provider.ts ; https://github.com/amicalhq/amical/blob/main/apps/desktop/src/pipeline/providers/transcription/whisper-prompt.ts ; https://github.com/amicalhq/amical/issues/170 ; https://github.com/k2-fsa/sherpa-onnx/blob/master/sherpa-onnx/python/csrc/offline-recognizer.cc ; https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-ctc/index.html

- PTT編集の切り分けでは、プレビューと最終ASRの区間結果、OpenCode要求のフラグ、出力の本文一致/類似度、箇条書き判定、marker検証を相関IDで記録すると、認識漏れ・指示配線漏れ・モデル無視・検証の見逃しを分けられる。ログには音声、本文、指示文、生成文を残さず、分類と数値メタデータだけにする。
- 修正・配布の確認（分類: PTT書式指示の要求配線、出典: `voice-command-diagnostics.jsonl`、`Services/VoiceCommandParser.cs`、`Services/TranscriptRewritePrompt.cs`、`Services/OpenCodeService.cs`、`MainWindow.xaml.cs`、Release build/publishと配置EXEのSHA-256、確認日: 2026-10-16 Asia/Tokyo、信頼度: 診断ログとソース/コンパイル/配布一致は高、修正版の実モデル動作は未確認、情報区分: ローカル確認・ユーザー再現ログ）: 11:25 JSTの箇条書き要求は`BulletListRequired=true`と検出されたが、要求生成APIに渡っておらず、応答にリストがないため例外と原文フォールバックになった。11:26 JSTのプロンプト整形要求は診断上は意図が検出された一方、実処理は`FormatAsPrompt=false`で通常編集として通った。診断用分類と実処理用分類を共有し、必須書式をSystem/User promptへ明示する。箇条書きが欠けた場合のみ1回再試行し、依然無効なら原文を保全する。既に整った原文の可能性があるため、類似度だけでプロンプト形式の成否を決めない。最終配布版は82,522,710 bytes、発行物と`dist/Better Flow.exe`のSHA-256は`E9FE8555A5BE640A76CE527846026F465A6736B650ACBF200DDC9CB0399BC5F5`で一致。旧診断版と中間版は`artifacts/BetterFlow-before-ptt-format-enforcement-20261016.exe`（SHA-256 `81E749E65D6CFFF33D1792598E191AE2CEEFE0A3E1C0A6793821A2FB7E6A6263`）および`artifacts/BetterFlow-before-ptt-format-enforcement-final-20261016.exe`（SHA-256 `F60CC45C11480F3A71855F75CB19350E0B4A03DA1487189F806B64AA2F097181`）へ退避。実マイク/モデルでの修正効果とテストスイートは未確認。
## 複数PTT音声編集操作の順序保持（2026-10-16）

- 内容: 同じSpace長押し区間、または複数Space区間で複数編集指示を出す場合、意図を区間ごとの順序付きoperation listとしてモデル要求へ渡し、marker順に適用する。複合処理でmarker未解決・命令復唱・本文未変更が起きた場合のみ、厳格な指示で一度だけ再要求する。
- 分類: PTT音声コマンドの構造化と限定再試行。
- 出典: Services/VoiceCommandParser.cs、Services/TranscriptRewritePrompt.cs、Services/OpenCodeService.cs、MainWindow.xaml.cs、Services/VoiceCommandDiagnosticLog.cs、2026-10-16 11:45〜11:48 JSTの本文非保存診断ログ。
- 確認日: 2026-10-16 Asia/Tokyo。
- 信頼度: コード・ビルド・発行EXEとのハッシュ一致は高。修正版の実モデル挙動は未確認。
- 情報区分: 原因は診断ログの未解決marker/復唱検出とソース構造から確認。operation listで直るという効果は実装上の意図であり、ユーザーの再テスト前なので未確認。
- 注意点: モデル応答の意味全体をコードだけで証明できない。音声本文や命令文はログへ保存しない。再要求は失敗時の最大1回に限定し、Gemini等のAPIリクエストを常時増やさない。
## 長文ストリーミングの末尾追従（2026-10-16）

- 内容: WPFでストリーミングテキストを毎回置き換えるとき、本文とScrollViewerのレイアウトを更新した後にScrollToEndを呼ぶ。ExtentHeightの変化を追従解除と判断する状態フラグがあると、文章が画面高を超えた瞬間に末尾追従が止まることがある。
- 分類: デスクトップUIのスクロール挙動。
- 出典: TranscriptPreviewWindow.xaml(.cs)、AnswerWindow.xaml(.cs)、2026-10-16の修正・Release build/publish・配布EXE照合。
- 確認日: 2026-10-16 Asia/Tokyo。
- 信頼度: 実装箇所、コンパイル、発行物と配布ファイルの一致は高。実際の長文ストリーミング表示は未確認。
- 情報区分: ソース確認・未確認事項。
- UI追記（内容: 末尾追従を維持したままストリーミング画面の縦スクロールバーをHiddenにする。分類: デスクトップUI。出典: AnswerWindow.xaml、TranscriptPreviewWindow.xaml、2026-10-16 Release build/publishとSHA-256照合。確認日: 2026-10-16 Asia/Tokyo。信頼度: コンパイル・配置一致は高、実画面は未確認。情報区分: ソース確認・未確認事項）。

## OpenCode検索ツールの限定許可（2026-10-16）

- 内容: Web検索と検索結果ページの取得を選択テキスト質問モードだけで使う場合、OpenCodeの組み込み`websearch`を有効にする環境変数と、質問専用agentの`permission`を組み合わせる。通常音声入力・音声編集・辞書判定は従来のbuild agentを使い、Webツールを利用させない。質問agentは`*`をdenyにしつつ、無料Zen要求の互換性を保つためグローバル同様の限定`read`（一時workspace内の固定ファイルだけ）と限定`bash`（`echo *`だけ）を明示し、`websearch`・`webfetch`だけを外部ツールとして許可する。
- 分類: OpenCodeツール権限とモード分離。
- 出典: `Services/OpenCodeService.cs`、OpenCode公式[Tools](https://docs.opencode.ai/docs/tools/)・[Permissions](https://docs.opencode.ai/docs/permissions/)ドキュメント、2026-10-16 Release build/publish・`dist/Better Flow.exe`のSHA-256照合。
- 確認日: 2026-10-16 Asia/Tokyo。
- 信頼度: ソースとコンパイル、発行EXE/配布EXEの一致は高。実モデルが検索・ページ取得を呼び出すかは未確認。
- 情報区分: 実装・ローカル確認・実動作未確認。
- OpenCode公式Toolsドキュメントでは`webfetch`はURLページ取得、`websearch`はWeb検索として説明され、両方を個別にallowできる（確認日: 2026-10-16）。
- 13:00ごろの実行ログ（確認日: 2026-10-16）では質問モデル`opencode/deepseek-v4.1-flash-free`が約1.6秒と0.4秒で`invalid-response`となり、応答本文は0文字だった。質問専用agentで`*` denyがグローバルの安全な`read`/`bash`許可も上書きしていたため、同じ限定許可をagent側にも明示した。セッションエラー本文はログに記録されず、因果の実モデル再確認は未実施。
- 配布結果: Release build/publish成功、0エラー・NU1900警告1件。最終EXEは82,526,665 bytes、SHA-256 `B4BB107FAEF8F8362897E35C5A3C27A3D43FFB4B2BC063869AF89890E173C4D9`。直前EXEは`artifacts/BetterFlow-before-question-mode-fix-20261016-130445417.exe`へ保存。最初の置換はファイルロックで失敗したが、Restart Managerにロック中プロセスがないことを確認後に再試行し、`dist/Better Flow.exe`を置換、発行物とハッシュ一致を確認。実行中の対象アプリはなく、停止PIDは0件。修正後の実モデル動作とテストスイートは未実施。

## UI Automation入力欄の初回捕捉と処理段階ゲージ（2026-10-16）

- 内容: UIAのFocusedElement親方向だけで入力欄を初期捕捉すると、Chromium/Electronが編集領域をTextPattern-only Document/Customで公開する場合や、フォーカス中要素が実際の編集パターンを持たない場合に見失う。対象HWND内の上限付き候補検索で、実際に貼り付けた文章が見える編集欄へ結び付ける。本文・音声は診断ログに書かず、候補数・検索理由・ControlTypeのみ記録し、曖昧一致では追跡を見送る。
- 分類: UI Automationのアクセシビリティ追跡・辞書自動学習。
- 出典: `Services/AutoDictionaryLearningMonitor.cs`、2026-10-16 Release build/publishと`dist\Better Flow.exe`のSHA-256確認。ChatGPT Desktop対象のローカル診断ログには該当失敗記録なし。
- 確認日: 2026-10-16 Asia/Tokyo。
- 信頼度: 変更した検索経路・コンパイル・EXE配置一致は高。実際のChatGPT Desktop再テストは未実施で、今回の報告の実行時根本原因が解消したかは未確認。
- 情報区分: ソース確認・環境ログ未確認・実機再テスト待ち。
- UI追記（内容: 下部カプセルの既存暗色背景とアイコン/文字の間に半透明の左寄せ塗りを置き、認識25%、OpenCode準備50%、生成/反映75%、完了100%とする共通段階を表示。これは経過時間の実測完了率ではない。分類: デスクトップUI。出典: `IndicatorWindow.xaml(.cs)`、`MainWindow.xaml.cs`、ユーザー添付画像、Release build/publishと配置EXE照合。確認日: 2026-10-16 Asia/Tokyo。信頼度: コンパイル・EXE配置一致は高、実画面の印象は未確認。情報区分: ソース確認・未確認事項）。
- 追記（内容: `IndicatorWindow`は25/50/75/100%を即時設定しているため、段階が変わるとゲージが跳ねる。OpenCode SSEにはbusy/idleと生成テキスト差分がある一方、総トークン数や完了見込みは現行イベント処理で得ていない。通常音声の書き換え・選択文編集には生成進捗コールバックも接続されていない。対応時は幅をイージングでアニメーションし、SSE活動から推定目標を少しずつ更新する。完了前は90〜95%程度で止め、結果検証・反映後だけ100%にする。これは実測完了率ではない。OpenCode以外の経路は専用の状態イベントがある範囲だけ反映する。分類: UI進捗表示。出典: `IndicatorWindow.xaml.cs`、`Services/OpenCodeService.cs`、`MainWindow.xaml.cs`の読み取り確認。確認日: 2026-10-16 Asia/Tokyo。信頼度: 現行コードと利用イベントは高、視覚的な体感効果は未確認。情報区分: 調査・実装前）。

## 連続VADセッション中のAI処理抑止と進捗ゲージ（2026-10-16）

- 内容: 質問/編集の連続録音では、認識結果を消費した直後にキュー・VADプリロール・進行中音声を消去し、NAudioの`WaveInEvent.StopRecording()`でマイク取得自体を停止する。`RecordingStopped`を待ってからAI処理し、待機へ戻る時だけ録音を再開する。フラグで音声を捨てるだけでは録音デバイスとOS側の取得が継続していたため、処理中の発話が次回入力に混ざる可能性を除けなかった。
- 技術的な注意点: NAudio 2.2.1の`WaveInEvent.StartRecording()`はcapture stateがStoppedであることを要求し、`StopRecording()`後にcapture threadが終了して`RecordingStopped`が通知される。再開時は通知を待ってから同じWaveInEventを開始する。
- UI: 成功後に待機表示へ戻す経路では`ShowStatus`で塗りをリセット・非表示にする。次の認識開始が独立したゲージセッションになる。進捗はSSE活動/累積応答文字数を使った推定目標で、WPFの`CompositionTarget.Rendering`ごとに経過時間ベースの指数補間を行う。正確な完了率ではない。
- 通常音声入力ではAI処理中の専用送信コマンド録音を停止。録音中に認識した送信指示は維持する。
- ファイル置換で共有違反が起きた場合、プロセスの実行パスを照合できない状態で画像名だけを使って停止しない。バックアップと発行物を検証し、対象プロセスが閉じた後に置換する。
- 分類: WPF音声進捗・VAD・Windows EXE置換。出典: `IndicatorWindow.xaml.cs`、`Services/ContinuousMicrophoneRecorder.cs`、`MainWindow.xaml.cs`、2026-10-16 Release publishとハッシュ照合。確認日: 2026-10-16 Asia/Tokyo。信頼度: コンパイルとハッシュは高、実マイク挙動は未確認。情報区分: 実装・実機未確認。

## 設定ウィンドウの表示倍率（2026-10-16）

- 内容: 設定画面は945×747のデザイン面を`Viewbox Stretch="Uniform"`で表示する。`ShowSettingsExperience()`でウィンドウ寸法を決める`SettingsPreferredScale`を0.75にすると、内容も縦横比を保って縮小される。作業領域が小さい場合は画面内に収まる倍率をさらに優先でき、設定画面だけに適用すればセットアップ画面には影響しない。
- 分類: WPF設定画面レイアウト。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、2026-10-16 Release publishと`dist/Better Flow.exe`のSHA-256照合。
- 確認日: 2026-10-16 Asia/Tokyo。
- 信頼度: ソース、ビルド、配置EXEの一致は高。実画面の見た目は起動して確認していない。
- 情報区分: ローカルソース確認・実画面未確認。

## 設定サイドバーのヒット領域とチェックボックス配色（2026-10-17）

- 内容: WPF `ToggleButton`の当たり判定だけサイドバー全幅へ広げ、表示中の選択ピルを保つには、ボタン本体を226×38pxの透明なBorderにし、幅104・高さ38pxのSurfaceを左Margin=51pxで置く。内側SurfaceのHeightを省略すると文字高まで縮み見た目が薄くなる。親キャンバスのLeft=0、Top値は変更せず、親Borderの右端を設定画面の開始x=226に合わせて隣の設定面と重ねない。
- チェックボックス: 未チェック時の`CheckCircle`は背景より明るい`#5C5C5C`、未チェックでホバー中は`#707070`、チェック済みは従来の`#E9E9E9`を使い、状態をMultiTriggerで分ける。
- 分類: WPFアクセシビリティ・ヒット領域・設定画面配色。
- 出典: `MainWindow.xaml`、2026-10-17 Release publishと`dist/Better Flow.exe`のSHA-256照合。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース、ビルド、配置EXEの一致は高。画面上の実クリックと見た目は未確認。
- 情報区分: ローカルソース確認・実画面未確認。

## 設定サイドバーのドラッグ移動（2026-10-17）

- 内容: WPF Canvas上の空白部分からWindowをドラッグ可能にするには、サイドバー幅に合わせた透明BorderをCanvasの子要素として先に置き、その後に既存の操作ボタンとコンテンツ面を描画する。空白部分はBorderにヒットし、前景のボタンが入力を受けるため、タブや閉じる操作を奪わない。`MouseLeftButtonDown`から既存の`DragMove()`処理を呼び、コンテンツ面の開始x=226に境界を合わせる。
- 分類: WPFウィンドウ操作・ヒットテスト。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、2026-10-17 Release publishと`dist/Better Flow.exe`のSHA-256照合。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース、ビルド、配置EXEの一致は高。実画面のドラッグとボタン操作は未確認。
- 情報区分: ローカルソース確認・実画面未確認。

## ユーザー設定プロンプトの適用（2026-10-17）

- 内容: 設定画面の「指示」ページで入力した追加指示は、`AppSettings.UserSystemPrompt`としてローカル設定に保存する。500文字上限は`StringInfo.ParseCombiningCharacters`でUnicodeテキスト要素単位に扱い、設定画面で残り文字数を表示する。
- UI入力時は、入力・改行・貼り付け後の文字数を事前確認し、500文字を超える操作全体を拒否する。末尾を切り詰めて既存の文章を消さない。TextChangedにも復旧用のガードを置く。入力欄のクリック時に黒枠が出ないようIsKeyboardFocusedの枠変更を外し、キーボード操作では薄い灰色のフォーカス表示を使う。
- 適用範囲: OpenCodeの共通`GenerateTextAsync`でsystem messageへ加えるため、音声整形、選択文編集・回答、辞書補正判定などのOpenCode要求に適用される。別経路のGPT Live質問セッションにも開始時のsession instructionsとして渡す。空欄なら既存system messageをそのまま使う。タスク固有の目的・出力形式・情報源・安全制約と衝突する場合はタスク固有指示を優先する。
- 分類: WPF設定UI・ローカル設定保存・AIプロンプト合成。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AppSettings.cs`、`Services/UserSystemPromptComposer.cs`、`Services/OpenCodeService.cs`、`Services/GptLiveQuestionSession.cs`。2026-10-17 Release build/publishと発行EXE・`dist/Better Flow.exe`のSHA-256一致を確認。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース・ビルド・EXE配置の確認は高。実画面の表示と実モデル応答への影響は未確認。
- 情報区分: ローカルソース確認・実UI/モデル未確認。

## 通常音声入力の60秒無音キャンセル（2026-10-17）

- 内容: 通常音声入力のPCMをSilero VADへ512サンプル（32ms）単位で流し、最後に発話を検出した時刻を単調時計で記録する。60秒間発話がなければ録音を停止し、ASR/LLM/貼り付け処理を呼ばず、録音バッファを破棄する。選択文の編集・質問に使う連続録音経路には適用しない。
- 設定: 一般ページ末尾のチェックボックスで有効/無効を保存する。初期値は有効。録音中に設定を変えようとした場合は拒否し、次回の録音から適用する。
- 分類: WPF音声入力・Silero VAD・録音バッファ破棄。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AppSettings.cs`、`Services/MicrophoneRecorder.cs`。2026-10-17 Release build/publishと配布EXEのSHA-256一致を確認。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース・ビルド・EXE配置は高。60秒間の実マイク動作と画面上の操作は未確認。
- 情報区分: ローカルソース確認・実マイク/実UI未確認。

## Better Flow設定のエクスポート/インポート（2026-10-17）

- 内容: 設定バックアップはバージョン付きJSON形式にし、`AppSettings`全体を含めることで、一般/詳細設定、辞書、辞書学習の保留・抑止情報、指示プロンプトをまとめて移行できる。GPT Live OAuth認証情報は別のローカルファイルに保存されるため出力対象に含めない。旧プロバイダー設定などのlegacy項目も除外する。
- インポートでは形式名・バージョン・サイズを確認し、ユーザー確認後に現行設定ファイルをタイムスタンプ付きでコピーしてから保存する。保存前にホットキーの有効性を確認し、保存後は設定UI、辞書、プロンプト、マイク、回答モードを再初期化する。OpenCodeモデル自体が変わった場合は準備状態を未完了へ戻し、モデル一覧の再取得を案内する。
- 分類: WPF設定移行・JSON形式・資格情報の分離。
- 出典: `Services/SettingsTransfer.cs`、`Services/AppSettings.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、2026-10-17 Release build/publishと配布EXEのSHA-256照合。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース、Release build/publish、配置EXEの一致は高。画面操作、実際の往復エクスポート/インポートは未確認。
- 情報区分: ユーザー要望・ローカル実装確認・実UI未確認。

## Better Flowの初期セットアップ用データ範囲（2026-10-17）

- 内容: `AppPaths.Root`は通常`%LOCALAPPDATA%\Better Flow`。旧版の`%LOCALAPPDATA%\AutoPrompt`が残る場合は、設定ファイルの有無に基づいてどちらかを選ぶか、旧フォルダーを移行する。実機パスを確認できない場合は両方を一括削除せず、`settings.json`の所在とコードの選択規則で現行rootを特定する。
- Better Flow管理のOpenCode実行ファイルは`opencode\opencode.exe`、ローカルASRは`models\parakeet-ja-int8\`、展開済みSilero VADは`models\silero-vad\`。OpenCodeモデルはダウンロードして保存せず、選択IDは`settings.json`に入り、OpenCode設定`opencode.json`は一時workspaceを作る`staging\workspace-*`内に置く。通常の停止処理でworkspaceは削除される。
- OpenCodeコマンドはPATH上の既存コマンドをBetter Flow同梱runtimeより先に探す。またプロバイダー認証/接続はOpenCode Desktopのプロファイルを参照するため、Better Flow専用runtimeや設定の削除ではDesktopの認証・モデル接続は初期化されない。WebView2プロファイル、GPT Live認証、ログ、WebView2インストーラー用downloadsはOpenCode再セットアップには不要なので保持を推奨する。
- 分類: Windowsアプリデータ配置・OpenCode runtime選択・セットアップ復旧。
- 出典: `Services/AppPaths.cs`、`Services/OpenCodeService.cs`、`Services/OpenCodeInstaller.cs`、`Services/Bootstrapper.cs`、`Services/SileroVoiceActivityDetector.cs`。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソースから確認した配置と選択順は高。実PCのAppData内のファイル存在・使用中rootはサンドボックス制約で未確認。
- 情報区分: ローカルソース確認・実PCの状態未確認。

## 統計ページの日本語入力集計（2026-10-17）

- 内容: 日本語には空白による単語区切りがないため、単語数と誤認しないよう統計ページは「総入力文字数」として表示する。`StringInfo.LengthInTextElements`で認識済みテキストの書記素数を集計し、PTT音声コマンドは通常音声入力の文字数・時間から除外する。
- 通常音声は16 kHz録音セグメントのサンプル数から時間を計算する。連続編集・質問はVADが切り出した発話サンプル、GPT Liveは認識イベントのspeech_started〜speech_stopped区間を使うため、AI応答処理時間は含めない。キャンセルや無音入力など、認識テキストのない操作は集計しない。
- 表示名、累計、256×256へ中央正方形クロップしたPNGプロフィール画像は`AppSettings`に保存され、設定エクスポートにも含まれる。画像は円形のEllipseに表示し、元ファイルパスや本文を統計へ保存しない。
- 分類: WPF設定UI・ローカル利用統計・日本語テキスト計数。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AppSettings.cs`、`Services/GptLiveQuestionSession.cs`、`Services/SettingsTransfer.cs`。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソースとRelease publishの成功は高。設定画面の実表示、実音声による数値更新、GPT Liveの実際のspeech_stoppedイベントは未確認。
- 情報区分: 実装・ソース確認・実機動作未確認。

## 設定画面のマイク入力レベル表示（2026-10-17）

- 内容: NAudio `WaveInEvent`のPCM16 RMS値をUIメーターへ渡す場合、録音処理と同じデバイス番号・16kHz monoを使い、ウィンドウの`Loaded`と`IsVisibleChanged`の両方から監視状態を同期する。デバイスが開けない場合に例外を握りつぶすと、静止したメーターとの区別がつかないため、設定欄に短い確認案内を表示する。
- 今回のレベル値はRMS 0.00025以下を無音として扱い、dBFS -68〜-26を0〜1へ正規化する。以前のゲート0.001では静かな発話がゼロになりやすかった。オーバーレイの音声レベルも同じ正規化関数を使うため、しきい値変更時は表示感度も変わる点に注意する。
- 設定行の横位置は、区切り線と同じ左右マージン35/17 DIPでStretchし、入力欄の列を`*`、間隔26 DIP、ボタン104 DIPとする。固定幅639 DIPと左マージン35 DIPだけを指定すると、WPFが余った幅を中央寄せし、実測では入力欄/ボタンが区切り線より9 DIP右へずれた。スクロールバーの有無でも余り幅が変わるため、幅の数値だけを合わせても整列を保証できない。
- メーターは既存19 DIPの見出し行へ置き、28 DIPの高さを上下-4.5 DIPの余白で重ねる。マイク欄の行構成19/23/Auto/3/Auto DIP、MinHeight=112、外側1 DIP区切り線を追加前へ戻すと、入力欄・区切り線・後続チェック項目の縦座標が追加前と一致する。見出しとの中心差はレイアウト丸めにより0.5 DIP以内。
- 分類: NAudio/WPF音量レベルUI・設定レイアウト。
- 出典: `MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/AudioInputLevel.cs`、`Services/MicrophoneLevelMonitor.cs`、Git `4f9c54b`との比較、`artifacts/settings-layout-validation.json`。WPFで実際のXAMLをMeasure/Arrangeして、通常/長い名称・スクロール有無の4条件で176項目を照合した。clean Release publishと配布EXEのSHA-256一致も確認。
- 確認日: 2026-10-17 Asia/Tokyo。
- 信頼度: ソース、WPFの実座標・ソースからの描画は高。実マイクの感度と通常起動したアプリの実画面は未確認。
- 情報区分: ローカル実装・WPF描画確認・実機動作未確認。
