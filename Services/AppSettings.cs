using System.IO;

namespace AutoPrompt.Services;

internal sealed class AppSettings
{
    public bool AiModeEnabled { get; set; } = false;
    public string AiProviderId { get; set; } = AiProviderCatalog.OpenCodeZenFree;
    public Dictionary<string, AiProviderSettings> AiProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string AiTermsAcceptedFor { get; set; } = string.Empty;
    public uint ShortcutModifiers { get; set; }
    public uint ShortcutVirtualKey { get; set; } = HotkeyBinding.RightAltVirtualKey;
}

internal sealed class SettingsStore
{
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();

            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                       File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureDirectories();
        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(settings));
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }
}
