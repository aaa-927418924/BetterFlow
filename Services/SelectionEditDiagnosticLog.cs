using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace BetterFlow.Services;

internal sealed record SelectionEditDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string Stage,
    bool TargetWindowForeground,
    string? ControlType,
    bool? HasKeyboardFocus,
    bool? IsKeyboardFocusable,
    string SelectionState,
    uint? InputEventsSent,
    uint? InputEventsRequested,
    int? InputLastErrorCode,
    bool? MatchedOriginalProvider,
    string? ExceptionType);

internal static class SelectionEditDiagnosticLog
{
    private const long MaximumLogBytes = 128 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static void Write(
        string stage,
        IntPtr targetWindow,
        AutomationElement? provider = null,
        string selectionState = "unknown",
        uint? inputEventsSent = null,
        uint? inputEventsRequested = null,
        int? inputLastErrorCode = null,
        bool? matchedOriginalProvider = null,
        string? exceptionType = null)
    {
        try
        {
            string? controlType = null;
            bool? hasKeyboardFocus = null;
            bool? isKeyboardFocusable = null;
            if (provider is not null)
            {
                try
                {
                    var current = provider.Current;
                    controlType = current.ControlType.ProgrammaticName;
                    hasKeyboardFocus = current.HasKeyboardFocus;
                    isKeyboardFocusable = current.IsKeyboardFocusable;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }

            var entry = new SelectionEditDiagnosticEntry(
                DateTimeOffset.UtcNow,
                stage,
                targetWindow != IntPtr.Zero && PasteService.GetForegroundWindowHandle() == targetWindow,
                controlType,
                hasKeyboardFocus,
                isKeyboardFocusable,
                selectionState,
                inputEventsSent,
                inputEventsRequested,
                inputLastErrorCode,
                matchedOriginalProvider,
                exceptionType);

            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var path = Path.Combine(AppPaths.Root, "selection-edit-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(false));

                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never affect audio capture, editing, or selection state.
        }
    }
}
