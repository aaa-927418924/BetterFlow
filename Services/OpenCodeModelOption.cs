namespace AutoPrompt.Services;

internal sealed record OpenCodeModelOption(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ModelName)
{
    public string DisplayName => $"{ProviderName} — {ModelName} ({ProviderId}/{ModelId})";
}

internal sealed class LegacyAiProviderSettings
{
    public string ModelId { get; set; } = string.Empty;
}
