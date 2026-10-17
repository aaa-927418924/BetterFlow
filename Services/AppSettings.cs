using System.IO;
using System.Text.Json.Serialization;

namespace BetterFlow.Services;

internal sealed class AppSettings
{
    public bool AiModeEnabled { get; set; } = true;
    public string UserSystemPrompt { get; set; } = string.Empty;
    public string SelectionEditEngine { get; set; } = "opencode";
    public string SelectedTextAnswerMode { get; set; } = "opencode";
    public bool DisableShortcutInFullscreen { get; set; } = true;
    public string OpenCodeModelProviderId { get; set; } = "opencode";
    public string OpenCodeModelId { get; set; } = "deepseek-v4.1-flash-free";
    public List<CorrectionDictionaryEntry> CorrectionDictionary { get; set; } = new();
    public List<DictionaryLearningEvidence> PendingDictionaryLearning { get; set; } = new();
    public List<string> SuppressedAutomaticDictionaryTerms { get; set; } = new();

    [JsonPropertyName("DictionaryLearningMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDictionaryLearningMode { get; set; }

    public string MicrophoneDeviceId { get; set; } = AudioInputDeviceOption.SystemDefaultId;
    public uint ShortcutModifiers { get; set; }
    public uint ShortcutVirtualKey { get; set; } = HotkeyBinding.RightAltVirtualKey;
    public bool ShowIndicatorHints { get; set; } = true;
    public bool AutoStopOnSilenceEnabled { get; set; } = true;
    public bool DiscordAutoMuteEnabled { get; set; }
    public uint DiscordMuteShortcutModifiers { get; set; }
    public uint DiscordMuteShortcutVirtualKey { get; set; }
    public string StatisticsDisplayName { get; set; } = Environment.UserName;
    public string StatisticsAvatarPngBase64 { get; set; } = string.Empty;

    // Usage totals are encrypted in the local settings file. Portable exports carry
    // the decrypted values in their archive manifest and intentionally omit this blob.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatisticsProtectedPayload { get; set; }

    [JsonIgnore]
    public long StatisticsTotalInputCharacters { get; set; }

    [JsonIgnore]
    public long StatisticsTotalInputMilliseconds { get; set; }

    [JsonIgnore]
    public bool StatisticsProtectionError { get; set; }

    [JsonIgnore]
    public string? StatisticsProtectionStatusMessage { get; set; }

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
        : $"{PreferredSpelling}\n誤認識: {string.Join("、", AlternativeSpellings)}";
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
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!string.IsNullOrWhiteSpace(settings.StatisticsProtectedPayload))
            {
                if (StatisticsDataProtection.TryUnprotect(
                        settings.StatisticsProtectedPayload,
                        out var statistics))
                {
                    settings.StatisticsTotalInputCharacters = statistics.TotalInputCharacters;
                    settings.StatisticsTotalInputMilliseconds = statistics.TotalInputMilliseconds;
                }
                else
                {
                    // Keep the unreadable blob intact when saving unrelated settings. A
                    // damaged stats record must not reset the rest of the user's settings.
                    settings.StatisticsProtectionError = true;
                    settings.StatisticsProtectionStatusMessage =
                        "統計データを復号できませんでした。設定をインポートすると復旧できます。";
                }
            }
            else
            {
                // Migrate totals written by earlier versions as plain JSON numbers.
                settings.StatisticsTotalInputCharacters = ReadLegacyNonNegativeLong(
                    root, "StatisticsTotalInputCharacters");
                settings.StatisticsTotalInputMilliseconds = ReadLegacyNonNegativeLong(
                    root, "StatisticsTotalInputMilliseconds");
            }

            var hasObsoleteSpeechModelSettings = false;
            var hasLegacyStatistics = false;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                hasObsoleteSpeechModelSettings = root.EnumerateObject().Any(property =>
                    property.Name.Equals("SpeechRecognitionModelId", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("SpeechRecognitionModelPreferenceVersion", StringComparison.OrdinalIgnoreCase));
                hasLegacyStatistics = HasProperty(root, "StatisticsTotalInputCharacters") ||
                                      HasProperty(root, "StatisticsTotalInputMilliseconds");
            }

            if (hasObsoleteSpeechModelSettings || (hasLegacyStatistics && settings.StatisticsProtectedPayload is null))
            {
                try
                {
                    Save(settings);
                    settings.StatisticsProtectionStatusMessage = null;
                }
                catch
                {
                    // Keep the old file intact if migration cannot be written. The app
                    // can still load and use its other settings, and a later save retries.
                    settings.StatisticsProtectionStatusMessage = "統計を暗号化して保存できませんでした。";
                }
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.StatisticsProtectionError)
        {
            settings.StatisticsProtectedPayload = StatisticsDataProtection.Protect(
                settings.StatisticsTotalInputCharacters,
                settings.StatisticsTotalInputMilliseconds);
            settings.StatisticsProtectionStatusMessage = null;
        }

        AppPaths.EnsureDirectories();
        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(settings));
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }

    private static bool HasProperty(System.Text.Json.JsonElement root, string name) =>
        root.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static long ReadLegacyNonNegativeLong(System.Text.Json.JsonElement root, string name)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return 0;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                property.Value.TryGetInt64(out var value))
                return Math.Max(0, value);
            return 0;
        }
        return 0;
    }
}
