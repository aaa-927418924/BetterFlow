namespace AutoPrompt.Services;

internal static class AiProviderCatalog
{
    public const string OpenCodeZenFree = "opencode-zen-free";
    public const string DeepSeek = "deepseek";
    public const string OpenRouter = "openrouter";
    public const string Gemini = "gemini";
    public const string OpenAiCompatible = "openai-compatible";

    public static IReadOnlyList<AiProviderOption> Options { get; } =
    [
        new(OpenCodeZenFree, "OpenCode Zen Free"),
        new(DeepSeek, "DeepSeek API"),
        new(OpenRouter, "OpenRouter API"),
        new(Gemini, "Gemini API"),
        new(OpenAiCompatible, "OpenAI互換API")
    ];

    public static bool UsesOpenCode(string providerId) => providerId == OpenCodeZenFree;

    public static AiProviderSettings CreateDefaultSettings(string providerId) => new()
    {
        ModelId = providerId == OpenCodeZenFree ? "muse-spark-1.3-contributor-free" : string.Empty,
        BaseUrl = providerId == OpenAiCompatible ? "https://api.openai.com/v1" : string.Empty
    };
}

internal sealed class AiProviderSettings
{
    public string ModelId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ProtectedApiKey { get; set; } = string.Empty;
}

internal sealed record AiProviderOption(string Id, string DisplayName);
internal sealed record ZenModelOption(string ModelId, string DisplayName);
