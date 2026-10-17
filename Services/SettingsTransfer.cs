using System.IO.Compression;
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
    public ExportedStatistics? Statistics { get; set; }
    public bool HasProfileAvatar { get; set; }
}

internal sealed class ExportedStatistics
{
    public long TotalInputCharacters { get; set; }
    public long TotalInputMilliseconds { get; set; }
}

internal static class SettingsTransfer
{
    public const string FormatName = "BetterFlow.Settings";
    public const int CurrentVersion = 2;
    private const int LegacyJsonVersion = 1;
    private const int MaximumSettingsJsonBytes = 2 * 1024 * 1024;
    private const int MaximumAvatarBytes = 512 * 1024;
    private const int MaximumArchiveBytes = 8 * 1024 * 1024;
    private const string SettingsEntryName = "settings.json";
    private const string AvatarEntryName = "profile/avatar.png";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    public static byte[] SerializeArchive(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.StatisticsProtectionError)
            throw new InvalidOperationException("統計データを読み込めないため、完全なエクスポートを作成できません。");

        // Round-trip to produce an independent snapshot, then omit machine-specific or
        // sensitive legacy provider data. OAuth credentials are stored separately.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? new AppSettings();
        snapshot.LegacyAiProviderId = null;
        snapshot.LegacyAiProviders = null;
        snapshot.LegacyDictionaryLearningMode = null;
        snapshot.StatisticsProtectedPayload = null;
        snapshot.StatisticsProtectionError = false;
        snapshot.StatisticsProtectionStatusMessage = null;
        snapshot.StatisticsAvatarPngBase64 = string.Empty;

        var avatarBytes = DecodeAvatar(settings.StatisticsAvatarPngBase64);
        var document = new SettingsExportDocument
        {
            Settings = snapshot,
            Statistics = new ExportedStatistics
            {
                TotalInputCharacters = Math.Max(0, settings.StatisticsTotalInputCharacters),
                TotalInputMilliseconds = Math.Max(0, settings.StatisticsTotalInputMilliseconds)
            },
            HasProfileAvatar = avatarBytes is not null
        };
        var settingsBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (settingsBytes.Length > MaximumSettingsJsonBytes)
            throw new InvalidDataException("設定データが大きすぎます。");

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteEntry(archive, SettingsEntryName, settingsBytes);
            if (avatarBytes is not null)
                WriteEntry(archive, AvatarEntryName, avatarBytes);
        }

        if (output.Length > MaximumArchiveBytes)
            throw new InvalidDataException("エクスポートしたファイルが大きすぎます。");
        return output.ToArray();
    }

    /// <summary>Reads the previous JSON export format for backward compatibility.</summary>
    public static AppSettings Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumSettingsJsonBytes)
            throw new InvalidDataException("設定ファイルが大きすぎます。");

        using var jsonDocument = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var exportDocument = JsonSerializer.Deserialize<SettingsExportDocument>(json, JsonOptions)
                             ?? throw new InvalidDataException("設定ファイルを読み込めませんでした。");
        ValidateHeader(exportDocument, LegacyJsonVersion);

        var settings = exportDocument.Settings
                       ?? throw new InvalidDataException("設定データが見つかりません。");
        var settingsElement = FindProperty(jsonDocument.RootElement, "Settings");
        settings.StatisticsTotalInputCharacters = ReadNonNegativeLong(settingsElement, "StatisticsTotalInputCharacters");
        settings.StatisticsTotalInputMilliseconds = ReadNonNegativeLong(settingsElement, "StatisticsTotalInputMilliseconds");
        settings.StatisticsProtectedPayload = null;
        settings.StatisticsProtectionError = false;
        settings.StatisticsProtectionStatusMessage = null;
        ValidateEmbeddedAvatar(settings);
        ClearLegacyProviderSettings(settings);
        return settings;
    }

    public static AppSettings DeserializeFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("設定ファイルが見つかりません。", path);
        if (file.Length > MaximumArchiveBytes)
            throw new InvalidDataException("設定ファイルが大きすぎます。");

        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[2];
        var signatureLength = input.Read(signature);
        input.Position = 0;
        if (signatureLength == 2 && signature[0] == (byte)'P' && signature[1] == (byte)'K')
            return DeserializeArchive(input);

        if (file.Length > MaximumSettingsJsonBytes)
            throw new InvalidDataException("設定ファイルが大きすぎます。");
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        return Deserialize(reader.ReadToEnd());
    }

    private static AppSettings DeserializeArchive(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        if (archive.Entries.Count is < 1 or > 2)
            throw new InvalidDataException("ZIP内のファイル構成が正しくありません。");

        ZipArchiveEntry? settingsEntry = null;
        ZipArchiveEntry? avatarEntry = null;
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, SettingsEntryName, StringComparison.Ordinal))
            {
                if (settingsEntry is not null)
                    throw new InvalidDataException("設定ファイルが重複しています。");
                settingsEntry = entry;
            }
            else if (string.Equals(entry.FullName, AvatarEntryName, StringComparison.Ordinal))
            {
                if (avatarEntry is not null)
                    throw new InvalidDataException("プロフィール画像が重複しています。");
                avatarEntry = entry;
            }
            else
            {
                throw new InvalidDataException("ZIPに未対応のファイルが含まれています。");
            }
        }

        if (settingsEntry is null)
            throw new InvalidDataException("ZIP内に設定データが見つかりません。");

        var settingsBytes = ReadEntryBounded(settingsEntry, MaximumSettingsJsonBytes);
        var document = JsonSerializer.Deserialize<SettingsExportDocument>(settingsBytes, JsonOptions)
                       ?? throw new InvalidDataException("設定データを読み込めませんでした。");
        ValidateHeader(document, CurrentVersion);

        var settings = document.Settings
                       ?? throw new InvalidDataException("設定データが見つかりません。");
        var statistics = document.Statistics
                         ?? throw new InvalidDataException("統計データが見つかりません。");
        if (statistics.TotalInputCharacters < 0 || statistics.TotalInputMilliseconds < 0)
            throw new InvalidDataException("統計データの値が正しくありません。");
        if (document.HasProfileAvatar != (avatarEntry is not null))
            throw new InvalidDataException("プロフィール画像と設定データの組み合わせが正しくありません。");

        settings.StatisticsTotalInputCharacters = statistics.TotalInputCharacters;
        settings.StatisticsTotalInputMilliseconds = statistics.TotalInputMilliseconds;
        settings.StatisticsProtectedPayload = null;
        settings.StatisticsProtectionError = false;
        settings.StatisticsProtectionStatusMessage = null;
        settings.StatisticsAvatarPngBase64 = string.Empty;
        if (avatarEntry is not null)
        {
            var avatarBytes = ReadEntryBounded(avatarEntry, MaximumAvatarBytes);
            ValidatePng(avatarBytes);
            settings.StatisticsAvatarPngBase64 = Convert.ToBase64String(avatarBytes);
        }

        ClearLegacyProviderSettings(settings);
        return settings;
    }

    private static void ValidateHeader(SettingsExportDocument document, int expectedVersion)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("Better Flowの設定ファイルではありません。");
        if (document.Version != expectedVersion)
            throw new InvalidDataException($"未対応の設定ファイル形式です (version {document.Version})。");
    }

    private static void ClearLegacyProviderSettings(AppSettings settings)
    {
        settings.LegacyAiProviderId = null;
        settings.LegacyAiProviders = null;
        settings.LegacyDictionaryLearningMode = null;
    }

    private static byte[]? DecodeAvatar(string? encodedAvatar)
    {
        if (string.IsNullOrWhiteSpace(encodedAvatar))
            return null;
        if (encodedAvatar.Length > ((MaximumAvatarBytes + 2) / 3) * 4)
            throw new InvalidDataException("プロフィール画像が大きすぎます。");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encodedAvatar);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("プロフィール画像を読み込めませんでした。", ex);
        }
        if (bytes.Length > MaximumAvatarBytes)
            throw new InvalidDataException("プロフィール画像が大きすぎます。");
        ValidatePng(bytes);
        return bytes;
    }

    private static void ValidateEmbeddedAvatar(AppSettings settings)
    {
        var avatarBytes = DecodeAvatar(settings.StatisticsAvatarPngBase64);
        settings.StatisticsAvatarPngBase64 = avatarBytes is null
            ? string.Empty
            : Convert.ToBase64String(avatarBytes);
    }

    private static void ValidatePng(byte[] bytes)
    {
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("プロフィール画像の形式が正しくありません。");
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static byte[] ReadEntryBounded(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException("ZIP内のファイルが大きすぎます。");

        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("ZIP内のファイルが大きすぎます。");
            output.Write(buffer, 0, read);
        }

        if (output.Length != entry.Length)
            throw new InvalidDataException("ZIP内のファイルが途中で切れています。");
        return output.ToArray();
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return default;
    }

    private static long ReadNonNegativeLong(JsonElement element, string name)
    {
        var value = FindProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? Math.Max(0, number)
            : 0;
    }
}
