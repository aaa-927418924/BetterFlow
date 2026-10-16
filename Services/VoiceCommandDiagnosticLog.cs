using System.IO;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed record VoiceCommandDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string OperationId,
    string Stage,
    string Outcome,
    int? SegmentIndex,
    string? Route,
    int InstructionLength,
    bool PromptFormattingIntent,
    bool BulletListIntent,
    bool TranslationIntent,
    int? TranscriptLength,
    int? ResponseLength,
    bool? RequestHasVoiceCommandSegments,
    bool? RequestHasEditInstructions,
    bool? FormatAsPrompt,
    bool? BulletListRequired,
    bool? ResponseHasBulletList,
    bool? ResponseMatchesTranscript,
    double? ResponseSimilarity,
    int? ExpectedCommandCount,
    int? EditOperationCount,
    int? ResolvedCommandCount,
    int? LineBreakOutcomeCount,
    int? AppliedOutcomeCount,
    int? UnresolvedOutcomeCount,
    string? ExceptionType);

internal static class VoiceCommandDiagnosticLog
{
    private const long MaximumLogBytes = 128 * 1024;
    private const int MaximumSimilarityCharacters = 4_000;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static void Write(
        string operationId,
        string stage,
        string outcome,
        int? segmentIndex = null,
        string? route = null,
        string? recognizedInstruction = null,
        IEnumerable<string>? editInstructions = null,
        string? transcript = null,
        string? response = null,
        bool? requestHasVoiceCommandSegments = null,
        bool? requestHasEditInstructions = null,
        bool? formatAsPrompt = null,
        bool? bulletListRequired = null,
        bool? responseHasBulletList = null,
        int? expectedCommandCount = null,
        int? editOperationCount = null,
        int? resolvedCommandCount = null,
        int? lineBreakOutcomeCount = null,
        int? appliedOutcomeCount = null,
        int? unresolvedOutcomeCount = null,
        Exception? exception = null)
    {
        try
        {
            var instructionParts = (editInstructions ?? Array.Empty<string>())
                .Append(recognizedInstruction ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            var instructionText = string.Join("\n", instructionParts);
            var intent = ClassifyInstruction(instructionText);
            var comparison = CompareTranscriptAndResponse(transcript, response);

            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var path = Path.Combine(AppPaths.Root, "voice-command-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(false));

                var entry = new VoiceCommandDiagnosticEntry(
                    DateTimeOffset.UtcNow,
                    operationId,
                    stage,
                    outcome,
                    segmentIndex,
                    route,
                    instructionText.Length,
                    intent.PromptFormatting,
                    intent.BulletList,
                    intent.Translation,
                    transcript?.Length,
                    response?.Length,
                    requestHasVoiceCommandSegments,
                    requestHasEditInstructions,
                    formatAsPrompt,
                    bulletListRequired,
                    responseHasBulletList,
                    comparison?.Matches,
                    comparison?.Similarity,
                    expectedCommandCount,
                    editOperationCount,
                    resolvedCommandCount,
                    lineBreakOutcomeCount,
                    appliedOutcomeCount,
                    unresolvedOutcomeCount,
                    exception?.GetBaseException().GetType().Name);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect voice processing or text output.
        }
    }

    private static (bool PromptFormatting, bool BulletList, bool Translation) ClassifyInstruction(string text)
    {
        var normalized = text.ToLowerInvariant();
        return (
            normalized.Contains("プロンプト", StringComparison.Ordinal) ||
            normalized.Contains("prompt", StringComparison.Ordinal),
            normalized.Contains("箇条書", StringComparison.Ordinal) ||
            normalized.Contains("リスト", StringComparison.Ordinal) ||
            normalized.Contains("bullet", StringComparison.Ordinal),
            normalized.Contains("翻訳", StringComparison.Ordinal) ||
            normalized.Contains("英訳", StringComparison.Ordinal) ||
            normalized.Contains("英語に", StringComparison.Ordinal) ||
            normalized.Contains("translate", StringComparison.Ordinal) ||
            normalized.Contains("translation", StringComparison.Ordinal));
    }

    private static (bool Matches, double Similarity)? CompareTranscriptAndResponse(string? transcript, string? response)
    {
        if (transcript is null || response is null)
            return null;

        var source = NormalizeForComparison(transcript);
        var result = NormalizeForComparison(response);
        var matches = string.Equals(source, result, StringComparison.Ordinal);
        if (source.Length > MaximumSimilarityCharacters)
            source = source[..MaximumSimilarityCharacters];
        if (result.Length > MaximumSimilarityCharacters)
            result = result[..MaximumSimilarityCharacters];

        if (source.Length < 2 || result.Length < 2)
            return (matches, matches ? 1d : 0d);

        var sourceNgrams = CreateBigrams(source);
        var resultNgrams = CreateBigrams(result);
        var intersection = sourceNgrams.Count(resultNgrams.Contains);
        var union = sourceNgrams.Count + resultNgrams.Count - intersection;
        var similarity = union == 0 ? (matches ? 1d : 0d) : (double)intersection / union;
        return (matches, Math.Round(similarity, 3));
    }

    private static string NormalizeForComparison(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static HashSet<string> CreateBigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length - 1; index++)
            result.Add(value.Substring(index, 2));
        return result;
    }
}
