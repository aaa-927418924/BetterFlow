using System.IO;
using System.Text.Json.Serialization;

namespace AutoPrompt.Services;

internal sealed class AppSettings
{
    public bool AiModeEnabled { get; set; } = false;
    public string OpenCodeModelProviderId { get; set; } = "opencode";
    public string OpenCodeModelId { get; set; } = string.Empty;
    public string AiTermsAcceptedFor { get; set; } = string.Empty;
    public string MicrophoneDeviceId { get; set; } = AudioInputDeviceOption.SystemDefaultId;
    public uint ShortcutModifiers { get; set; }
    public uint ShortcutVirtualKey { get; set; } = HotkeyBinding.RightAltVirtualKey;

    [JsonPropertyName("AiProviderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiProviderId { get; set; }

    [JsonPropertyName("AiProviders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, LegacyAiProviderSettings>? LegacyAiProviders { get; set; }
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
