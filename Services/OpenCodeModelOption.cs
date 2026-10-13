namespace BetterFlow.Services;

internal sealed record OpenCodeModelOption(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ModelName)
{
    public string DisplayName => $"{ProviderName} — {ModelName}";

    public override string ToString() => DisplayName;
}

internal sealed class LegacyAiProviderSettings
{
    public string ModelId { get; set; } = string.Empty;
}
