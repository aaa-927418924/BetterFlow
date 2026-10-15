using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal static partial class VoiceCommandParser
{
    private static readonly char[] CommandSeparators = [' ', '\t', '\u3000', '、', ',', '，', '.', '．'];
    private const string ExplicitLineBreakMarker = "__BETTERFLOW_EXPLICIT_LINE_BREAK__";
    private const string VoiceCommandMarkerPrefix = "__BETTERFLOW_VOICE_COMMAND_";
    public const string PromptFormatInstruction = "発話した内容の意味と要望を保ったまま、プロンプトとして使いやすく整えてください。";

    public static string CreateVoiceCommandMarker(string sessionId, int index) =>
        $"{VoiceCommandMarkerPrefix}{sessionId}_{index:D4}__";

    public static bool HasUnresolvedVoiceCommandMarker(string text) => VoiceCommandMarkerRegex().IsMatch(text);

    public static string RemoveVoiceCommandMarkers(string text)
    {
        if (!VoiceCommandMarkerRegex().IsMatch(text))
            return text;

        var withoutMarkers = VoiceCommandMarkerRegex().Replace(text, string.Empty);
        return Regex.Replace(withoutMarkers, "[ \\t]{2,}", " ").Trim(' ', '\t', '\r');
    }

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
        command = normalized switch
        {
            "ここから改行して" or "ここで改行して" or "ここから改行してください" or "ここで改行してください" or
                "ここから改行" or "ここで改行" or "ここに改行して" or "ここで改行を入れて" or
                "改行を入れて" or "改行入れて" or "改行してください" or "改行してね" or "改行して" or "改行" =>
                VoiceTextCommand.InsertNewLine,
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

    [GeneratedRegex("__BETTERFLOW_VOICE_COMMAND_[a-f0-9]{32}_\\d{4,}__", RegexOptions.CultureInvariant)]
    private static partial Regex VoiceCommandMarkerRegex();
}

internal enum VoiceTextCommand
{
    None,
    InsertNewLine,
    FormatAsPrompt,
    SendAsIs
}

internal sealed record VoiceEditCommandInstruction(string Marker, string RecognizedText);
