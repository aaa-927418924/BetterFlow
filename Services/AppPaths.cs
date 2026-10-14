using System.IO;

namespace BetterFlow.Services;

internal static class AppPaths
{
    private static readonly string ApplicationDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string LegacyRootPath = Path.Combine(ApplicationDataRoot, "AutoPrompt");
    private static readonly string BetterFlowRootPath = Path.Combine(ApplicationDataRoot, "Better Flow");

    public static string Root { get; } = ResolveRoot();

    public static string ModelDirectory => Path.Combine(Root, "models", "parakeet-ja-int8");
    public static string ModelFile => Path.Combine(ModelDirectory, "model.int8.onnx");
    public static string TokensFile => Path.Combine(ModelDirectory, "tokens.txt");
    public static string WhisperModelDirectory => Path.Combine(Root, "models", "whisper-large-v3-turbo-int8");
    public static string WhisperEncoderFile => Path.Combine(WhisperModelDirectory, "turbo-encoder.int8.onnx");
    public static string WhisperDecoderFile => Path.Combine(WhisperModelDirectory, "turbo-decoder.int8.onnx");
    public static string WhisperTokensFile => Path.Combine(WhisperModelDirectory, "turbo-tokens.txt");
    public static string WhisperModelMarkerFile => Path.Combine(WhisperModelDirectory, "model.ready");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ModelSetupLockFile => Path.Combine(Root, "model-setup.lock");
    public static string DownloadDirectory => Path.Combine(Root, "downloads");
    public static string TemporaryDirectory => Path.Combine(Root, "staging");
    public static string OpenCodeDirectory => Path.Combine(Root, "opencode");
    public static string OpenCodeExecutable => Path.Combine(OpenCodeDirectory, "opencode.exe");
    public static string OpenCodeSetupLockFile => Path.Combine(Root, "opencode-setup.lock");
    public static string GptLiveCredentialFile => Path.Combine(Root, "gpt-live-credentials.bin");
    public static string WebView2UserDataDirectory => Path.Combine(Root, "webview2");
    public static string LiveWebAssetsDirectory => Path.Combine(Root, "live-web");
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelDirectory);
        Directory.CreateDirectory(WhisperModelDirectory);
        Directory.CreateDirectory(DownloadDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }

    private static string ResolveRoot()
    {
        if (!Directory.Exists(LegacyRootPath))
            return BetterFlowRootPath;

        if (!Directory.Exists(BetterFlowRootPath))
        {
            try
            {
                Directory.Move(LegacyRootPath, BetterFlowRootPath);
                return BetterFlowRootPath;
            }
            catch (IOException)
            {
                // A running copy of the old app may still hold a file open. Keep
                // using its existing data directory; the next launch can retry.
                return LegacyRootPath;
            }
            catch (UnauthorizedAccessException)
            {
                return LegacyRootPath;
            }
        }

        var currentSettingsExist = File.Exists(Path.Combine(BetterFlowRootPath, "settings.json"));
        var legacySettingsExist = File.Exists(Path.Combine(LegacyRootPath, "settings.json"));
        return currentSettingsExist || !legacySettingsExist ? BetterFlowRootPath : LegacyRootPath;
    }
}
