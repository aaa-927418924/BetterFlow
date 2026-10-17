# Better Flow

Better Flow is a voice dictation and selected-text editing app for Windows x64. It transcribes Japanese speech locally with Parakeet 0.6B Japanese, then refines the text with a model available through OpenCode while preserving your speaking style.

Press Right Alt and speak to enter text. If you select text before activating Better Flow, you can edit the selection or ask questions about it.

## Key Features

- **Voice dictation**: Removes filler words and false starts, then pastes the result into the original input field. A provisional transcript appears while you speak; after recording stops, Better Flow generates the final text from the complete recording.
- **Continuous editing of selected text**: Edit a selection with spoken instructions such as "Translate this into English" or "Make this shorter." The text stays selected after each edit so you can continue with another instruction.
- **Ask questions about selected text**: Select text on a web page or in an AI response and ask a question such as "Summarize this in three lines." Answers stream into a panel at the bottom of the screen without changing the original text. You can select and copy an answer.
- **Custom terminology dictionary**: Add correct spellings and examples of misrecognitions manually. When you correct and submit dictated text, Better Flow can also register likely spelling or terminology corrections for the same term automatically.
- **Usage statistics**: View the total number of recognized input characters and microphone input time in Settings. Set a display name and profile image; the image is center-cropped into a circle. The ZIP settings export includes the statistics, display name, and PNG avatar.
- **Undo**: Undo the most recent change with the button shown after an edit or spoken command in an editing session.
- **Settings and background operation**: Configure the shortcut, microphone, and OpenCode model. Better Flow can run in the system tray and start automatically with Windows.

Text refinement is always enabled. Better Flow uses the same user profile as OpenCode Desktop and lets you choose models from connected providers. Your model choice is saved across restarts. Better Flow does not have separate API key or base URL fields.

## First-Time Setup

1. Run the distributed standalone EXE.
2. Click **Setup**. Better Flow downloads the Parakeet Japanese speech model and installs the OpenCode CLI if needed.
3. If your chosen model requires authentication, sign in to its provider through OpenCode, for example, in OpenCode Desktop.
4. Once the speech model and OpenCode model are ready, press the keyboard shortcut to start using Better Flow.

Initial setup requires an internet connection and about 1.2 GB of free space for the Parakeet model. The model is not bundled with the EXE and is downloaded during setup.

If OpenCode is already available on your PATH, Better Flow uses that installation first. Otherwise, it installs the official Windows x64 baseline release into the application's data folder. The default model ID for a new setup is `opencode/deepseek-v4.1-flash-free`. Existing model selections are preserved. Available models and authentication requirements depend on OpenCode's offerings.

After setup, Better Flow starts in the system tray without opening the settings window. Double-click the tray icon to open Settings. Closing the window keeps the app running in the background. To exit completely or change whether Better Flow starts with Windows, use the tray icon's right-click menu.

## Usage

The default shortcut key is **Right Alt**.

| Context | Action |
| --- | --- |
| Input field with no text selected | Press Right Alt to start recording. Press it again to stop, refine, and paste the text. |
| Selected text in an editable input field | Press Right Alt to start continuous editing. Each edit is applied when you finish speaking. Press it again to cancel. |
| Selected text on a web page or other non-editable content | Press Right Alt to start asking questions. Each answer is generated when you finish speaking, and you can continue with follow-up questions. Press it again to end the session. |
| While regular dictation is being refined | Press Right Alt to skip the AI refinement and paste the Parakeet transcript. |

Continuous editing and question-answering through OpenCode use Silero VAD to detect speech segments. Stop regular dictation manually with the shortcut. The provisional transcript is for reference and may differ from the final text pasted into the input field.

During regular dictation, saying "Send it" or "Submit it" makes Better Flow press Enter after pasting the refined text. The result depends on how the target application handles Enter, so check the destination before using this feature.

While regular dictation is recording, hold **Space** to enter voice-command mode temporarily. Speech recorded while Space is held is treated as a command and excluded from the dictated text. Supported text commands include:

- "ここから改行して" ("Insert a line break here"): add a line break at that point in the dictated text.
- "プロンプト風の文章に整形して" ("Format this as a prompt"): rewrite the dictated text as a clear prompt without adding new requirements.
- "そのまま送信して" ("Send it as-is"): paste the raw transcript without AI rewriting, then submit it.

Space is reserved while voice-command mode is active and does not type a space. These commands are available only during regular dictation; they do not control other apps and are unavailable during selected-text editing or question sessions.

Open **Dictionary List** in Settings to view registered terms. Clear terminology corrections are registered after one occurrence; ambiguous candidates require three separately confirmed corrections. Deleted terms are excluded from automatic relearning unless you explicitly re-enable it for an individual entry. Changes to a sentence's meaning or numeric values are not automatically treated as dictionary entries.

## Voice Conversations About Selected Text

When asking questions about selected text, you can choose **GPT Live** in Settings instead of the default OpenCode text responses. This mode lets you sign in to ChatGPT and have a spoken conversation about the selected text. The response is also displayed as it streams in.

GPT Live integration is experimental. Editing selected text still uses OpenCode. GPT Live does not use local Silero VAD. If the WebView2 Runtime is missing, Better Flow installs it with an installer whose Microsoft signature is verified.

## Data Handling

- **When using OpenCode**: Audio recording and transcription are processed locally on your PC. Transcripts, selected text, spoken instructions, and the context needed to answer questions are sent to the selected model through a local OpenCode server.
- **Automatic dictionary registration**: Better Flow sends the text before and after a manual correction, along with brief surrounding context, to the selected model to determine whether it is a terminology correction. Accepted spellings, example misrecognitions, and related entries are stored in local settings.
- **When using GPT Live**: Selected text and microphone audio are sent through ChatGPT's voice conversation service. OAuth tokens are stored locally and protected with Windows DPAPI.
- **Storage location**: Each Windows user's models, settings, dictionary entries, and diagnostic logs are stored in `%LOCALAPPDATA%\Better Flow`. Existing `AutoPrompt` data is migrated. If migration cannot be completed, for example because files are in use, the app continues to use the previous folder.
- **Usage statistics**: Totals for recognized text elements and input duration are encrypted with Windows DPAPI for the current user before being saved in `settings.json`. Transcript text is not retained for these statistics. Older plaintext totals are encrypted the next time the settings file is migrated or saved. Profile images are resized and center-cropped locally to a 256×256 PNG. Settings export creates a ZIP containing `settings.json` and, if set, `profile/avatar.png`. Importing the ZIP restores the display name, totals, and image, then encrypts the totals for the current Windows user. Existing JSON exports can still be imported.
- **Connections and authentication**: OpenCode manages provider connections and authentication. Better Flow's OpenCode server listens on the loopback interface and uses a password generated at each launch. The app attempts to delete temporary sessions after processing.

Diagnostic logs record processing stages, elapsed time, character counts, and similar metadata. They do not include audio transcript contents or dictionary candidates. Data retention and usage policies depend on the model providers and OpenCode. See the [OpenCode documentation](https://opencode.ai/docs/).

## Build from Source

Requirements: Windows x64 and the **.NET 8 SDK**.

```powershell
git clone https://github.com/aaa-927418924/BetterFlow.git
cd BetterFlow
dotnet restore AutoPrompt.csproj
dotnet publish AutoPrompt.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

The output is `artifacts/publish/BetterFlow.exe`, a standalone, self-contained executable that includes the .NET runtime and can be distributed directly. The speech model and OpenCode are downloaded during first-time setup on the recipient's PC.

This Git repository does not include generated EXEs, the Parakeet transcription model, user settings, or authentication credentials. Initial setup and any required provider authentication must be completed on the recipient's machine. The small Silero VAD model is included in the repository and the EXE.

The startup sound effect is optional. It is included in the build only when `Assets/universfield-gadget-activation-250072.mp3` is present locally. This file is excluded from Git. The app can be built without it; in that case, no startup sound is played.

### Regenerating Icons

Generate the PNG used in the UI and the ICO used for the window, taskbar, and EXE from the root-level `icon.svg`:

```powershell
pwsh -NoProfile -File tools/Generate-Icons.ps1
```

## Speech Models and Dependencies

- **Japanese ASR**: The sherpa-onnx INT8 ONNX version of Parakeet TDT-CTC 0.6B JA.
- **Speech detection**: A Sherpa-ONNX-compatible ONNX version of [Silero VAD](https://github.com/snakers4/silero-vad). The model and its [MIT license notice](Assets/SileroVAD-LICENSE.txt) are included.
- **Audio input/output**: NAudio.
- **Model inference**: sherpa-onnx.
- **Voice conversation interface**: Microsoft WebView2.
- **Archive extraction during setup**: SharpCompress.

The speech model was selected with reference to the [public Japanese FLEURS benchmark](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark). The published benchmark uses different model weights and a different execution environment from Better Flow's INT8 ONNX version, so equivalent recognition accuracy is not guaranteed.
