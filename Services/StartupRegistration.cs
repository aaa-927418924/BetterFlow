using Microsoft.Win32;

namespace BetterFlow.Services;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Better Flow";
    private const string LegacyValueName = "AutoPrompt";
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
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key is null) return false;

                var currentCommand = BuildCommand();
                var registeredCommand = key.GetValue(ValueName) as string;
                if (string.Equals(registeredCommand, currentCommand, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                    return true;
                }

                var legacyCommand = key.GetValue(LegacyValueName) as string;
                if (string.IsNullOrWhiteSpace(legacyCommand)) return false;

                // The old Run value belongs to this app. Move it to the renamed
                // entry and point it at the Better Flow executable now in use.
                key.SetValue(ValueName, currentCommand, RegistryValueKind.String);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                return true;
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
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("アプリの実行ファイルの場所を取得できませんでした。");

        return $"\"{executablePath}\" {TrayArgument}";
    }
}
