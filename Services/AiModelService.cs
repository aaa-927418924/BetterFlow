using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AutoPrompt.Services;

internal sealed class AiModelService : IDisposable
{
    private const string DeepSeekEndpoint = "https://api.deepseek.com/chat/completions";
    private const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<string> RewriteAsync(string providerId, AiProviderSettings settings, string apiKey,
        string transcript, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelId))
            throw new InvalidOperationException("モデルIDを入力してください。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("APIキーを入力してモデル設定を保存してください。");

        using var request = providerId == AiProviderCatalog.Gemini
            ? CreateGeminiRequest(settings.ModelId, apiKey, transcript)
            : CreateChatRequest(providerId, settings, apiKey, transcript);
        using var response = await _http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(BuildError(providerId, response.StatusCode, responseBody, apiKey), null, response.StatusCode);

        using var document = JsonDocument.Parse(responseBody);
        var result = providerId == AiProviderCatalog.Gemini
            ? ExtractGeminiText(document.RootElement)
            : ExtractChatText(document.RootElement);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException("選択したAIサービスから文章応答がありませんでした。モデルIDと利用状況を確認してください。");

        return result.Trim();
    }

    public void Dispose() => _http.Dispose();

    private static HttpRequestMessage CreateChatRequest(string providerId, AiProviderSettings settings,
        string apiKey, string transcript)
    {
        var endpoint = providerId switch
        {
            AiProviderCatalog.DeepSeek => new Uri(DeepSeekEndpoint),
            AiProviderCatalog.OpenRouter => new Uri(OpenRouterEndpoint),
            AiProviderCatalog.OpenAiCompatible => BuildCompatibleEndpoint(settings.BaseUrl),
            _ => throw new InvalidOperationException("選択したAIサービスには対応していません。")
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonBody(new
        {
            model = settings.ModelId,
            messages = new[]
            {
                new { role = "system", content = TranscriptRewritePrompt.SystemMessage },
                new { role = "user", content = TranscriptRewritePrompt.CreateUserMessage(transcript) }
            },
            temperature = 0.2,
            max_tokens = 1024
        });
        return request;
    }

    private static HttpRequestMessage CreateGeminiRequest(string modelId, string apiKey, string transcript)
    {
        var cleanModelId = modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;
        var endpoint = new Uri(GeminiEndpoint + Uri.EscapeDataString(cleanModelId) + ":generateContent");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        request.Content = JsonBody(new
        {
            systemInstruction = new { parts = new[] { new { text = TranscriptRewritePrompt.SystemMessage } } },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = TranscriptRewritePrompt.CreateUserMessage(transcript) } } }
            },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 1024 }
        });
        return request;
    }

    private static Uri BuildCompatibleEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback))
            throw new InvalidOperationException("OpenAI互換APIにはHTTPSを指定してください。HTTPはlocalhostなどのローカル接続だけ利用できます。");

        if (!string.IsNullOrEmpty(parsed.UserInfo))
            throw new InvalidOperationException("ベースURLにユーザー名やパスワードを含めないでください。");

        var builder = new UriBuilder(parsed);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            builder.Path = path + "/chat/completions";
        return builder.Uri;
    }

    private static string ExtractChatText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return string.Empty;
        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
            return string.Empty;
        return ReadTextParts(content);
    }

    private static string ExtractGeminiText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return string.Empty;
        var candidate = candidates[0];
        return candidate.TryGetProperty("content", out var content) &&
               content.TryGetProperty("parts", out var parts)
            ? ReadTextParts(parts)
            : string.Empty;
    }

    private static string ReadTextParts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
                parts.Add(part.GetString() ?? string.Empty);
            else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString() ?? string.Empty);
        }
        return string.Join("\n", parts);
    }

    private static string BuildError(string providerId, HttpStatusCode statusCode, string body, string apiKey)
    {
        var service = providerId switch
        {
            AiProviderCatalog.DeepSeek => "DeepSeek",
            AiProviderCatalog.OpenRouter => "OpenRouter",
            AiProviderCatalog.Gemini => "Gemini",
            _ => "OpenAI互換API"
        };
        var advice = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "APIキーと利用権限を確認してください。",
            HttpStatusCode.NotFound => "モデルIDとAPIのエンドポイントを確認してください。",
            HttpStatusCode.TooManyRequests => "利用上限またはレート制限を確認して、時間をおいて再試行してください。",
            _ => "モデル設定とサービスの状態を確認してください。"
        };
        var detail = ExtractErrorMessage(body);
        if (!string.IsNullOrWhiteSpace(apiKey)) detail = detail.Replace(apiKey, "[APIキー]", StringComparison.Ordinal);
        if (detail.Length > 300) detail = detail[..300] + "…";
        return $"{service}への要求に失敗しました (HTTP {(int)statusCode})。{advice}" +
               (string.IsNullOrWhiteSpace(detail) ? string.Empty : " 詳細: " + detail);
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? string.Empty;
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                    return message.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("message", out var topLevelMessage)) return topLevelMessage.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
