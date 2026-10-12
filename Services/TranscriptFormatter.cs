using System.Text.RegularExpressions;

namespace AutoPrompt.Services;

internal static partial class TranscriptFormatter
{
    public static string NormalizeTranscript(string transcript)
    {
        var normalized = transcript.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
