using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal static partial class VoiceCommandParser
{
    private static readonly char[] CommandSeparators = [' ', '\t', '\u3000', '、', ',', '，', '.', '．'];
    private static readonly HashSet<string> InsertNewLineCommands = new(StringComparer.Ordinal)
    {
        "ここから改行して", "ここで改行して", "ここから改行してください", "ここで改行してください",
        "ここから改行", "ここで改行", "ここに改行して", "ここで改行を入れて",
        "改行を入れて", "改行入れて", "改行してください", "改行してね", "改行して", "改行",
        "ここからかいぎょうして", "ここでかいぎょうして", "かいぎょうしてください", "かいぎょうして", "かいぎょう",
        "かいぎょうをいれて"
    };
    private const string ExplicitLineBreakMarker = "__BETTERFLOW_EXPLICIT_LINE_BREAK__";
    public const string PromptFormatInstruction = "発話した内容の意味と要望を保ったまま、プロンプトとして使いやすく整えてください。";

    public static string MarkExplicitLineBreaks(string transcript) =>
        transcript.Replace("\n", ExplicitLineBreakMarker, StringComparison.Ordinal);

    public static int CountExplicitLineBreaks(string transcript) =>
        transcript.Count(character => character == '\n');

    public static int CountExplicitLineBreakMarkers(string transcript) =>
        ExplicitLineBreakMarkerRegex().Matches(transcript).Count;

    public static string RestoreExplicitLineBreaks(string transcript) =>
        ExplicitLineBreakMarkerRegex().Replace(transcript, "\n");

    public static bool TryParseVoiceTextCommand(string transcript, out VoiceTextCommand command)
    {
        var normalized = NormalizeVoiceCommand(transcript);
        if (IsLikelyLineBreakCommand(normalized))
        {
            command = VoiceTextCommand.InsertNewLine;
            return true;
        }

        command = normalized switch
        {
            "プロンプト風の文章に整形して" or "文章をプロンプト風に整形して" or
                "プロンプト風に整形して" or "プロンプト風にして" or "プロンプト形式に整形して" or
                "プロンプト向けに整えて" =>
                VoiceTextCommand.FormatAsPrompt,
            "そのまま送信して" or "そのまま送って" or "そのまま送信までやって" or "そのまま送信までやってください" or
                "そのまま送ってください" or "そのまま送信してください" or
                "文字起こしのまま送って" or "文字起こしのまま送信して" or
                "そのまま入力欄に送信して" or "そのまま入力して送信して" or "そのまま送信" =>
                VoiceTextCommand.SendAsIs,
            _ => VoiceTextCommand.None
        };

        return command != VoiceTextCommand.None;
    }

    private static bool IsLikelyLineBreakCommand(string normalized)
    {
        if (InsertNewLineCommands.Contains(normalized))
            return true;

        // Japanese ASR often writes the homophone 開業 for 改行. Correct it only
        // while checking a short, dedicated Space PTT command phrase.
        var homophoneCorrected = normalized.Replace("開業", "改行", StringComparison.Ordinal);
        if (InsertNewLineCommands.Contains(homophoneCorrected))
            return true;

        return normalized.Length >= 4 && InsertNewLineCommands.Any(candidate =>
            candidate.Length >= 4 && IsWithinOneEdit(normalized, candidate));
    }

    private static bool IsWithinOneEdit(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
            return false;

        var leftIndex = 0;
        var rightIndex = 0;
        var edits = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            if (++edits > 1)
                return false;

            if (left.Length == right.Length)
            {
                leftIndex++;
                rightIndex++;
            }
            else if (left.Length > right.Length)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        if (leftIndex < left.Length || rightIndex < right.Length)
            edits++;

        return edits <= 1;
    }

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

    private static string NormalizeVoiceCommand(string transcript)
    {
        var normalized = new string((transcript ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character is not
                ('、' or ',' or '，' or '。' or '．' or '.' or '!' or '！' or '?' or '？' or '…' or '「' or '」' or '『' or '』'))
            .ToArray());
        return normalized.Replace("下さい", "ください", StringComparison.Ordinal);
    }

    [GeneratedRegex("(?:送信\\s*して|送っ\\s*て)(?:\\s*(?:ください|下さい))?[。！？!?…．.」』）)]*\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SendDirectiveAtEndRegex();

    [GeneratedRegex("__BETTERFLOW_EXPLICIT_LINE_BREAK__", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitLineBreakMarkerRegex();
}

internal enum VoiceTextCommand
{
    None,
    InsertNewLine,
    FormatAsPrompt,
    SendAsIs
}
