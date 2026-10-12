using System.Text.RegularExpressions;

namespace AutoPrompt.Services;

internal static partial class PromptFormatter
{
    public static string FitWithinTenLines(string response)
    {
        var text = response.Trim();
        text = FenceRegex().Replace(text, string.Empty).Trim();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 10) return string.Join(Environment.NewLine, lines);

        var groups = new List<string>(10);
        for (var i = 0; i < 10; i++)
        {
            var start = i * lines.Length / 10;
            var end = (i + 1) * lines.Length / 10;
            groups.Add(string.Join(" ", lines[start..end]));
        }
        return string.Join(Environment.NewLine, groups);
    }

    public static string PlainFallback(string transcript)
    {
        var normalized = transcript.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex("```[^\\n]*\\n?|```", RegexOptions.CultureInvariant)]
    private static partial Regex FenceRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
