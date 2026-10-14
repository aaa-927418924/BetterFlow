using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterFlow.Services;

internal sealed record DictionaryLearningDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string Stage,
    string Detail,
    int Count,
    string? MonitorId = null,
    long? CandidateId = null);

internal static class DictionaryLearningDiagnosticLog
{
    private const long MaximumLogBytes = 128 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(
        string stage,
        string detail = "none",
        int count = 0,
        string? monitorId = null,
        long? candidateId = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var path = Path.Combine(AppPaths.Root, "dictionary-learning-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(false));

                var entry = new DictionaryLearningDiagnosticEntry(
                    DateTimeOffset.UtcNow, stage, detail, count, monitorId, candidateId);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect transcription or dictionary learning.
        }
    }
}
