# Better Flow

Better Flow is a voice dictation and selected-text editing app for Windows x64. It transcribes Japanese speech locally using Parakeet 0.6B Japanese, then refines the text with a model accessed through OpenCode while preserving your speaking style.

Simply press Right Alt and speak to enter text. If you select text before activating Better Flow, you can edit the selection or ask questions about it.

## Key Features

- **Voice dictation**: Cleans up filler words and false starts, then pastes the result into the original input field. A provisional transcript appears during recording; after recording stops, the final result is generated from the complete audio.
- **Continuous editing of selected text**: Replace selected text using spoken instructions such as "Translate this into English" or "Make this shorter." The text remains selected after each edit, so you can give additional instructions.
- **Ask questions about selected text**: Select text on a web page or in an AI response and ask questions such as "Summarize this in three lines." Answers stream into a panel at the bottom of the screen without modifying the original text. You can select and copy the answers.
- **Custom terminology dictionary**: Manually register correct spellings and example misrecognitions. When you manually correct dictated text and submit it, Better Flow can also automatically register corrections identified as spelling or terminology fixes for the same term.
- **Undo**: Revert the most recent change using the Undo button shown after an edit or a spoken command during an editing session.
- **Settings and background operation**: Configure the shortcut, microphone, and OpenCode model. Better Flow supports system tray operation and automatic startup with Windows.

Text refinement is always enabled. Better Flow uses the same user profile as OpenCode Desktop and lets you choose models from connected providers. Your model selection persists across restarts. There are no separate API key or base URL fields in Better Flow.

## First-Time Setup

1. Launch the distributed standalone EXE.
2. Click **Setup**. Better Flow downloads the Parakeet Japanese speech model and, if necessary, the OpenCode CLI.
3. If your chosen model requires authentication, sign in through OpenCode, such as OpenCode Desktop.
4. Once the speech model and OpenCode model are ready, you can start using Better Flow with the keyboard shortcut.

The initial setup requires an internet connection and approximately 1.2 GB of free space for the Parakeet model. The model is not bundled with the EXE; it is downloaded during setup.

If OpenCode is already available on your PATH, Better Flow uses that installation first. Otherwise, it installs the official Windows x64 baseline release into the application's data folder. The default model ID is `opencode/muse-spark-1.3-contributor-free`. Available models and authentication requirements depend on OpenCode's offerings.

After setup, Better Flow starts in the system tray without opening the settings window. Double-click the tray icon to open settings. Closing the window keeps the app running in the background. To exit completely or toggle startup with Windows, use the tray icon's right-click menu.

## Usage

The default shortcut is **Right Alt**.

| Context | Action |
| --- | --- |
| Input field with no text selected | Press Right Alt to start recording. Press it again to stop, refine, and paste the text. |
| Selected text in an editable input field | Press Right Alt to start continuous editing. Edits are applied at speech boundaries. Press it again to cancel. |
| Selected text on a web page or other non-editable content | Press Right Alt to start asking questions. Answers are generated at speech boundaries, and you can continue asking follow-up questions. Press it again to end the session. |
| While regular dictation is being refined | Press Right Alt to cancel processing. |

Continuous editing and question-answering through OpenCode use Silero VAD to detect speech segments. Regular dictation is stopped manually using the shortcut. The provisional transcript is for reference and may differ from the final text pasted into the input field.

During regular dictation, saying "Send it" or "Submit it" causes Better Flow to press Enter after pasting the refined text. The result depends on how the target application handles Enter, so check the destination before using this feature.

Open **Dictionary List** in settings to view registered terms. Clear terminology corrections are registered after one occurrence; ambiguous candidates require three separate confirmed corrections. Deleted terms are excluded from automatic relearning unless you explicitly re-enable it for individual entries in the dictionary list. Changes to a sentence's meaning or numeric values are not automatically treated as dictionary entries.

## Voice Conversations About Selected Text

When asking questions about selected text, you can choose **GPT Live** in settings instead of the default OpenCode text responses. This additional mode lets you sign in to ChatGPT and have a spoken conversation about the selected text. Response text is also displayed as it streams in.

GPT Live integration is experimental. Editing selected text still uses OpenCode. Local Silero VAD is not applied to GPT Live. If the WebView2 Runtime is missing, Better Flow installs it using an installer whose Microsoft signature is verified.

## Data Handling

- **When using OpenCode**: Audio recording and transcription are processed locally on your PC. Transcripts, selected text, spoken instructions, and context needed to answer questions are sent to the selected model through a local OpenCode server.
- **Automatic dictionary registration**: Better Flow sends the text before and after manual corrections, along with a short surrounding context, to the selected model to determine whether a terminology correction was made. Accepted spellings, example misrecognitions, and related entries are stored in local settings.
- **When using GPT Live**: Selected text and microphone audio are sent through ChatGPT's voice conversation service. OAuth tokens are stored locally and protected using Windows DPAPI.
- **Storage location**: Models, settings, dictionary entries, and diagnostic logs are stored in `%LOCALAPPDATA%\Better Flow` for each Windows user. Existing `AutoPrompt` data is migrated. If migration cannot be completed, for example because files are in use, the previous folder continues to be used.
- **Connections and authentication**: Provider connections and authentication are managed by OpenCode. Better Flow's OpenCode server listens on the loopback interface and uses a password generated on each launch. The app attempts to delete temporary sessions after processing.

Diagnostic logs record processing stages, elapsed time, character counts, and similar metadata. They do not log the contents of audio transcripts or dictionary candidates. Data retention and usage policies of model providers and OpenCode depend on the respective services. See the [OpenCode documentation](https://opencode.ai/docs/).

## Build from Source

Requirements: Windows x64 and the **.NET 8 SDK**.

```powershell
git clone https://github.com/aaa-927418924/BetterFlow.git
cd BetterFlow
dotnet restore AutoPrompt.csproj
dotnet publish AutoPrompt.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

The output is `artifacts/publish/BetterFlow.exe`. This is a standalone, self-contained EXE that includes the .NET runtime and can be distributed directly. The speech model and OpenCode are downloaded during first-time setup on the recipient's PC.

This Git repository does not include generated EXEs, the Parakeet transcription model, user settings, or authentication credentials. Initial setup and any required provider authentication must also be completed on the recipient's machine. The small Silero VAD model is included in both the repository and the EXE.

The startup sound effect is optional. It is included in the build only if `Assets/universfield-gadget-activation-250072.mp3` is present locally. This file is excluded from Git. The app can be built without it, in which case no startup sound is played.

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

The speech model was selected with reference to the [public Japanese FLEURS benchmark](https://github.com/cm-nakamura-shogo/japanese-asr-l40s-benchmark). The published benchmark uses different model weights and an execution environment that differs from the INT8 ONNX version used in Better Flow, so equivalent recognition accuracy is not guaranteed.
