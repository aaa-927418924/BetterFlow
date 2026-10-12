using Microsoft.Win32;

namespace AutoPrompt.Services;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoPrompt";
    private const string TrayArgument = "--tray";

    public static bool IsTrayLaunchRequested => Environment.GetCommandLineArgs()
        .Skip(1)
        .Any(argument => string.Equals(argument, TrayArgument, StringComparison.OrdinalIgnoreCase));

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var registeredCommand = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(registeredCommand) &&
                       string.Equals(registeredCommand, BuildCommand(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windowsの自動起動設定を開けませんでした。");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("アプリの実行ファイルの場所を取得できませんでした。");

        return $"\"{executablePath}\" {TrayArgument}";
    }
}
