using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal static partial class VoiceCommandParser
{
    private static readonly char[] CommandSeparators = [' ', '\t', '\u3000', '、', ',', '，', '.', '．'];
    private const string ExplicitLineBreakMarker = "__BETTERFLOW_EXPLICIT_LINE_BREAK__";
    private const string VoiceCommandMarkerPrefix = "__BETTERFLOW_VOICE_COMMAND_";
    private const string VoiceCommandLineBreakMarkerPrefix = "__BETTERFLOW_VOICE_COMMAND_LINE_BREAK_";
    private const string VoiceCommandAppliedMarkerPrefix = "__BETTERFLOW_VOICE_COMMAND_APPLIED_";
    private const string VoiceCommandUnresolvedMarkerPrefix = "__BETTERFLOW_VOICE_COMMAND_UNRESOLVED_";
    public const string PromptFormatInstruction = "発話した内容の意味と要望を保ったまま、プロンプトとして使いやすく整えてください。";

    public static string CreateVoiceCommandMarker(string sessionId, int index) =>
        $"{VoiceCommandMarkerPrefix}{sessionId}_{index:D4}__";

    public static string CreateVoiceCommandLineBreakMarker(string marker) =>
        CreateVoiceCommandOutcomeMarker(marker, VoiceCommandLineBreakMarkerPrefix);

    public static string CreateVoiceCommandAppliedMarker(string marker) =>
        CreateVoiceCommandOutcomeMarker(marker, VoiceCommandAppliedMarkerPrefix);

    public static string CreateVoiceCommandUnresolvedMarker(string marker) =>
        CreateVoiceCommandOutcomeMarker(marker, VoiceCommandUnresolvedMarkerPrefix);

    public static bool HasUnresolvedVoiceCommandMarker(string text) =>
        VoiceCommandMarkerRegex().IsMatch(text) || VoiceCommandUnresolvedMarkerRegex().IsMatch(text);

    public static bool HasExactlyOneResolvedMarkerPerCommand(
        string text,
        IReadOnlyList<VoiceEditCommandInstruction> commands)
    {
        if (HasUnresolvedVoiceCommandMarker(text))
            return false;

        var outcomeMarkers = VoiceCommandOutcomeMarkerRegex().Matches(text);
        if (outcomeMarkers.Count != commands.Count)
            return false;

        foreach (var command in commands)
        {
            var lineBreakCount = CountOccurrences(text, CreateVoiceCommandLineBreakMarker(command.Marker));
            var appliedCount = CountOccurrences(text, CreateVoiceCommandAppliedMarker(command.Marker));
            if (lineBreakCount + appliedCount != 1)
                return false;
        }

        return true;
    }

    public static string RemoveVoiceCommandMarkers(string text)
    {
        if (!VoiceCommandMarkerRegex().IsMatch(text))
            return text;

        var withoutMarkers = VoiceCommandMarkerRegex().Replace(text, string.Empty);
        return Regex.Replace(withoutMarkers, "[ \\t]{2,}", " ").Trim(' ', '\t', '\r');
    }

    public static string RestoreVoiceCommandOutcomes(
        string text,
        IReadOnlyList<VoiceEditCommandInstruction> commands)
    {
        var lineBreakMarkerAlternation = string.Join("|", commands
            .Select(command => Regex.Escape(CreateVoiceCommandLineBreakMarker(command.Marker))));
        if (lineBreakMarkerAlternation.Length > 0)
        {
            var lineBreakMarkerRun = new Regex(
                $"[ \\t\\r\\n]*(?:{lineBreakMarkerAlternation})(?:[ \\t\\r\\n]*(?:{lineBreakMarkerAlternation}))*[ \\t\\r\\n]*",
                RegexOptions.CultureInvariant);
            text = lineBreakMarkerRun.Replace(text, match =>
            {
                var count = Regex.Matches(match.Value, lineBreakMarkerAlternation, RegexOptions.CultureInvariant).Count;
                return new string('\n', count);
            });
        }

        foreach (var command in commands)
            text = text.Replace(CreateVoiceCommandAppliedMarker(command.Marker), string.Empty, StringComparison.Ordinal);

        return Regex.Replace(text, "[ \\t]{2,}", " ").Trim(' ', '\t', '\r');
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
                "プロンプトに整形して" or "プロンプトとして整形して" or
                "プロンプトに整形してください" or "プロンプトとして整形してください" or
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

    public static string RemoveEchoedVoiceCommandText(
        string response,
        string originalTranscript,
        IReadOnlyList<VoiceEditCommandInstruction> commands,
        out bool removedEcho)
    {
        removedEcho = false;
        var sourceText = RemoveVoiceCommandMarkers(originalTranscript);
        var normalizedSource = NormalizeVoiceCommand(sourceText);
        var sanitized = response;

        foreach (var command in commands)
        {
            var normalizedCommand = NormalizeVoiceCommand(command.RecognizedText);
            if (normalizedCommand.Length < 4 || normalizedSource.Contains(normalizedCommand, StringComparison.OrdinalIgnoreCase))
                continue;

            var echoPattern = string.Join(
                @"[\s、,，。．.!！?？…]*",
                normalizedCommand.Select(character => Regex.Escape(character.ToString())));
            var regex = new Regex(echoPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var next = regex.Replace(sanitized, string.Empty);
            if (!string.Equals(next, sanitized, StringComparison.Ordinal))
            {
                removedEcho = true;
                sanitized = next;
            }
        }

        if (!removedEcho)
            return response;

        sanitized = Regex.Replace(sanitized, @"[ \t]{2,}", " ");
        sanitized = Regex.Replace(sanitized, @"[ \t]*([、,，。．.!！?？])", "$1");
        sanitized = Regex.Replace(sanitized, @"([、,，。．.!！?？])(?:[ \t]*[、,，。．.!！?？])+", "$1");
        return sanitized.Trim(' ', '\t', '\r', '\n');
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

    private static string CreateVoiceCommandOutcomeMarker(string marker, string outcomePrefix)
    {
        if (!marker.StartsWith(VoiceCommandMarkerPrefix, StringComparison.Ordinal) ||
            !VoiceCommandMarkerRegex().IsMatch(marker))
            throw new ArgumentException("音声コマンドmarkerの形式が正しくありません。", nameof(marker));

        return outcomePrefix + marker[VoiceCommandMarkerPrefix.Length..];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [GeneratedRegex("(?:送信\\s*して|送っ\\s*て)(?:\\s*(?:ください|下さい))?[。！？!?…．.」』）)]*\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SendDirectiveAtEndRegex();

    [GeneratedRegex("__BETTERFLOW_EXPLICIT_LINE_BREAK__", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitLineBreakMarkerRegex();

    [GeneratedRegex("__BETTERFLOW_VOICE_COMMAND_[a-f0-9]{32}_\\d{4,}__", RegexOptions.CultureInvariant)]
    private static partial Regex VoiceCommandMarkerRegex();

    [GeneratedRegex("__BETTERFLOW_VOICE_COMMAND_UNRESOLVED_[a-f0-9]{32}_\\d{4,}__", RegexOptions.CultureInvariant)]
    private static partial Regex VoiceCommandUnresolvedMarkerRegex();

    [GeneratedRegex("__BETTERFLOW_VOICE_COMMAND_(?:LINE_BREAK|APPLIED)_[a-f0-9]{32}_\\d{4,}__", RegexOptions.CultureInvariant)]
    private static partial Regex VoiceCommandOutcomeMarkerRegex();
}

internal enum VoiceTextCommand
{
    None,
    InsertNewLine,
    FormatAsPrompt,
    SendAsIs
}

internal sealed record VoiceEditCommandInstruction(string Marker, string RecognizedText);
