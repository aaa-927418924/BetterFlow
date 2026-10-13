using System.IO;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed record OpenCodeTimingEntry(
    DateTimeOffset TimestampUtc,
    string ProviderId,
    string ModelId,
    long GateWaitMs,
    long ModelCheckMs,
    long SessionCreateMs,
    long PromptAckMs,
    long? TimeToFirstTextMs,
    long ModelRequestMs,
    long SessionDeleteMs,
    long TotalMs,
    int ResponseChars,
    string Outcome,
    string FailureKind);

internal static class OpenCodeTimingLog
{
    private const long MaximumLogBytes = 256 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static void Write(OpenCodeTimingEntry entry)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var path = Path.Combine(AppPaths.Root, "opencode-timing.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(false));

                var line = JsonSerializer.Serialize(entry, JsonOptions);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect transcription or editing.
        }
    }
}
