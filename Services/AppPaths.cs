using System.IO;

namespace AutoPrompt.Services;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoPrompt");

    public static string OpenCodeDirectory => Path.Combine(Root, "opencode");
    public static string OpenCodeExecutable => Path.Combine(OpenCodeDirectory, "opencode.exe");
    public static string OpenCodeHome => Path.Combine(Root, "opencode-home");
    public static string OpenCodeData => Path.Combine(Root, "opencode-data");
    public static string ModelDirectory => Path.Combine(Root, "models", "parakeet-ja-int8");
    public static string ModelFile => Path.Combine(ModelDirectory, "model.int8.onnx");
    public static string TokensFile => Path.Combine(ModelDirectory, "tokens.txt");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string SecretFile => Path.Combine(Root, "zen-key.dpapi");
    public static string DownloadDirectory => Path.Combine(Root, "downloads");
    public static string TemporaryDirectory => Path.Combine(Root, "staging");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(OpenCodeDirectory);
        Directory.CreateDirectory(OpenCodeHome);
        Directory.CreateDirectory(OpenCodeData);
        Directory.CreateDirectory(ModelDirectory);
        Directory.CreateDirectory(DownloadDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }
}
