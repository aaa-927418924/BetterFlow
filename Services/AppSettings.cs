using System.IO;
using System.Text.Json.Serialization;

namespace BetterFlow.Services;

internal sealed class AppSettings
{
    public bool AiModeEnabled { get; set; } = true;
    public string SelectionEditEngine { get; set; } = "opencode";
    public string SelectedTextAnswerMode { get; set; } = "opencode";
    public string OpenCodeModelProviderId { get; set; } = "opencode";
    public string OpenCodeModelId { get; set; } = "muse-spark-1.3-contributor-free";
    public List<CorrectionDictionaryEntry> CorrectionDictionary { get; set; } = new();
    public bool AutoLearnDictionaryEnabled { get; set; } = true;
    public List<DictionaryLearningEvidence> PendingDictionaryLearning { get; set; } = new();
    public List<string> SuppressedAutomaticDictionaryTerms { get; set; } = new();

    [JsonPropertyName("DictionaryLearningMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDictionaryLearningMode { get; set; }

    [JsonIgnore]
    public bool LegacyDictionaryAlternativesPresent { get; set; }
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

internal sealed class CorrectionDictionaryEntry
{
    public string PreferredSpelling { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    [JsonIgnore]
    public string Summary => PreferredSpelling;
}

internal sealed class DictionaryLearningEvidence
{
    public string PreferredSpelling { get; set; } = string.Empty;
    public int IndependentCorrectionCount { get; set; }
    public DateTimeOffset LastCorrectionUtc { get; set; }
    public List<string> CountedSubmissionIds { get; set; } = new();
}

internal sealed class SettingsStore
{
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            using var document = System.Text.Json.JsonDocument.Parse(json);
            settings.LegacyDictionaryAlternativesPresent = document.RootElement.TryGetProperty(
                "CorrectionDictionary", out var dictionary) && dictionary.ValueKind == System.Text.Json.JsonValueKind.Array &&
                dictionary.EnumerateArray().Any(entry => entry.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    entry.TryGetProperty("Alternatives", out _));
            return settings;
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
