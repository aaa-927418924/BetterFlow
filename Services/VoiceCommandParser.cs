using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal static partial class VoiceCommandParser
{
    private static readonly char[] CommandSeparators = [' ', '\t', '\u3000', '、', ',', '，', '.', '．'];

    public static bool TryRemoveSendDirective(string transcript, out string content)
    {
        var match = SendDirectiveAtEndRegex().Match(transcript);
        if (!match.Success)
        {
            content = transcript;
            return false;
        }

        content = transcript[..match.Index].TrimEnd(CommandSeparators);
        return true;
    }

    public static bool IsStandaloneSendDirective(string transcript) =>
        TryRemoveSendDirective(transcript, out var content) && string.IsNullOrWhiteSpace(content);

    [GeneratedRegex("(?:送信\\s*して|送っ\\s*て)(?:\\s*(?:ください|下さい))?[。！？!?…．.」』）)]*\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SendDirectiveAtEndRegex();
}
