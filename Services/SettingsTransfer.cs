using System.IO;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed class SettingsExportDocument
{
    public string Format { get; set; } = SettingsTransfer.FormatName;
    public int Version { get; set; } = SettingsTransfer.CurrentVersion;
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public AppSettings? Settings { get; set; }
}

internal static class SettingsTransfer
{
    public const string FormatName = "BetterFlow.Settings";
    public const int CurrentVersion = 1;
    private const int MaximumImportBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Round-trip to produce an independent snapshot, then omit legacy provider data.
        // OAuth credentials are stored separately and are intentionally never included.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? new AppSettings();
        snapshot.LegacyAiProviderId = null;
        snapshot.LegacyAiProviders = null;
        snapshot.LegacyDictionaryLearningMode = null;

        return JsonSerializer.Serialize(new SettingsExportDocument
        {
            Settings = snapshot
        }, JsonOptions);
    }

    public static AppSettings Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumImportBytes)
            throw new InvalidDataException("設定ファイルが大きすぎます。");

        var document = JsonSerializer.Deserialize<SettingsExportDocument>(json, JsonOptions)
                       ?? throw new InvalidDataException("設定ファイルを読み込めませんでした。");
        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("Better Flowの設定ファイルではありません。");
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"未対応の設定ファイル形式です (version {document.Version})。");

        var settings = document.Settings
                       ?? throw new InvalidDataException("設定データが見つかりません。");
        settings.LegacyAiProviderId = null;
        settings.LegacyAiProviders = null;
        settings.LegacyDictionaryLearningMode = null;
        return settings;
    }

    public static AppSettings DeserializeFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("設定ファイルが見つかりません。", path);
        if (file.Length > MaximumImportBytes)
            throw new InvalidDataException("設定ファイルが大きすぎます。");

        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
    }
}
