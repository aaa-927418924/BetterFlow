using System.IO;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed record AudioInputDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string Stage,
    string Outcome,
    string? ExceptionType,
    int? HResult,
    bool HasRecognizedText,
    int RecognizedTextLength);

internal static class AudioInputDiagnosticLog
{
    private const long MaximumLogBytes = 128 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static void Write(string stage, string outcome, Exception? exception = null, string? recognizedText = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var path = Path.Combine(AppPaths.Root, "audio-input-diagnostic.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(false));

                var rootException = exception?.GetBaseException();
                var entry = new AudioInputDiagnosticEntry(
                    DateTimeOffset.UtcNow,
                    stage,
                    outcome,
                    rootException?.GetType().Name,
                    rootException?.HResult,
                    !string.IsNullOrWhiteSpace(recognizedText),
                    recognizedText?.Length ?? 0);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect audio processing or output.
        }
    }
}
