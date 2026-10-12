using System.IO;

namespace AutoPrompt.Services;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoPrompt");

    public static string ModelDirectory => Path.Combine(Root, "models", "parakeet-ja-int8");
    public static string ModelFile => Path.Combine(ModelDirectory, "model.int8.onnx");
    public static string TokensFile => Path.Combine(ModelDirectory, "tokens.txt");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ModelSetupLockFile => Path.Combine(Root, "model-setup.lock");
    public static string DownloadDirectory => Path.Combine(Root, "downloads");
    public static string TemporaryDirectory => Path.Combine(Root, "staging");
    public static string OpenCodeDirectory => Path.Combine(Root, "opencode");
    public static string OpenCodeExecutable => Path.Combine(OpenCodeDirectory, "opencode.exe");
    public static string OpenCodeSetupLockFile => Path.Combine(Root, "opencode-setup.lock");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelDirectory);
        Directory.CreateDirectory(DownloadDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }
}
