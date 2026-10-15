using System.IO;
using System.Text.Json.Serialization;

namespace BetterFlow.Services;

internal sealed class AppSettings
{
    public bool AiModeEnabled { get; set; } = true;
    public string SelectionEditEngine { get; set; } = "opencode";
    public string SelectedTextAnswerMode { get; set; } = "opencode";
    public string OpenCodeModelProviderId { get; set; } = "opencode";
    public string OpenCodeModelId { get; set; } = "deepseek-v4.1-flash-free";
    public bool UsePrecedingTextForAiRewrite { get; set; }
    public List<CorrectionDictionaryEntry> CorrectionDictionary { get; set; } = new();
    public List<DictionaryLearningEvidence> PendingDictionaryLearning { get; set; } = new();
    public List<string> SuppressedAutomaticDictionaryTerms { get; set; } = new();

    [JsonPropertyName("DictionaryLearningMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDictionaryLearningMode { get; set; }

    public string MicrophoneDeviceId { get; set; } = AudioInputDeviceOption.SystemDefaultId;
    public uint ShortcutModifiers { get; set; }
    public uint ShortcutVirtualKey { get; set; } = HotkeyBinding.RightAltVirtualKey;
    public bool DiscordAutoMuteEnabled { get; set; }
    public uint DiscordMuteShortcutModifiers { get; set; }
    public uint DiscordMuteShortcutVirtualKey { get; set; }

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
    public string Alternatives { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> AlternativeSpellings => (Alternatives ?? string.Empty)
        .Split(['、', ',', '，', ';', '；', '\n', '\r', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => value.Length > 0 && !string.Equals(value, PreferredSpelling, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToArray();

    [JsonIgnore]
    public string Summary => AlternativeSpellings.Count == 0
        ? PreferredSpelling
        : $"{PreferredSpelling}\n誤認識・別表記: {string.Join("、", AlternativeSpellings)}";
}

internal sealed class DictionaryLearningEvidence
{
    public string PreferredSpelling { get; set; } = string.Empty;
    public string Alternatives { get; set; } = string.Empty;
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
            var hasObsoleteSpeechModelSettings = false;
            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    hasObsoleteSpeechModelSettings = document.RootElement.EnumerateObject().Any(property =>
                        property.Name.Equals("SpeechRecognitionModelId", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("SpeechRecognitionModelPreferenceVersion", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (hasObsoleteSpeechModelSettings)
                Save(settings);
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
