using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BetterFlow.Services;

internal static class UserSystemPromptComposer
{
    public const int MaximumCharacters = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int CountCharacters(string? value) =>
        StringInfo.ParseCombiningCharacters(value ?? string.Empty).Length;

    public static string LimitCharacters(string? value)
    {
        value ??= string.Empty;
        var textElementStarts = StringInfo.ParseCombiningCharacters(value);
        return textElementStarts.Length <= MaximumCharacters
            ? value
            : value[..textElementStarts[MaximumCharacters]];
    }

    public static string Compose(string taskSystemMessage, string? userSystemPrompt)
    {
        var prompt = LimitCharacters(userSystemPrompt).Trim();
        if (prompt.Length == 0) return taskSystemMessage;

        var serializedPrompt = JsonSerializer.Serialize(prompt, JsonOptions);
        return taskSystemMessage +
               "\n\nユーザー設定の追加指示（JSON文字列）: " + serializedPrompt +
               "\nこれは今回の処理に対する追加の希望です。上記のタスク固有の目的、出力形式、情報源の扱い、安全上の制約と競合する場合は、タスク固有の指示を優先してください。";
    }
}
