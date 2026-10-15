using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

/// <summary>
/// Best-effort access to a small amount of user-authored text immediately before
/// the caret in the foreground editor. The returned value is transient and must
/// not be written to logs or settings.
/// </summary>
internal static partial class PrecedingTextContextService
{
    private const int MaximumRawCharacters = 1_200;
    private const int MaximumContextCharacters = 280;
    private const int MaximumFocusedAncestorDepth = 12;
    private const int MaximumWindowAncestorDepth = 20;

    public static string? Capture(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || PasteService.GetForegroundWindowHandle() != targetWindow)
            return null;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || !IsWithinWindow(focused, targetWindow))
                return null;

            var walker = TreeWalker.ControlViewWalker;
            var element = focused;
            for (var depth = 0; element is not null && depth < MaximumFocusedAncestorDepth; depth++)
            {
                if (!IsWithinWindow(element, targetWindow))
                    return null;

                if (TryGetEditableTextPattern(element, out var textPattern))
                {
                    var ranges = textPattern.GetSelection();
                    if (ranges is null || ranges.Length != 1)
                        return null;

                    var caretOrSelection = ranges[0];
                    if (caretOrSelection.CompareEndpoints(TextPatternRangeEndpoint.Start, caretOrSelection,
                            TextPatternRangeEndpoint.End) != 0)
                        return null;

                    var preceding = textPattern.DocumentRange.Clone();
                    preceding.MoveEndpointByRange(TextPatternRangeEndpoint.Start, caretOrSelection,
                        TextPatternRangeEndpoint.Start);
                    preceding.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character,
                        -MaximumRawCharacters);
                    preceding.MoveEndpointByRange(TextPatternRangeEndpoint.End, caretOrSelection,
                        TextPatternRangeEndpoint.Start);

                    var rawText = preceding.GetText(-1);
                    var normalized = Normalize(rawText);
                    if (normalized.Length == 0 || !normalized.Any(char.IsLetterOrDigit))
                        return null;

                    var placeholderCandidates = new[]
                    {
                        element.Current.Name,
                        element.Current.HelpText
                    };
                    if (placeholderCandidates.Any(candidate =>
                            string.Equals(Normalize(candidate), normalized, StringComparison.OrdinalIgnoreCase)))
                        return null;

                    return KeepRecentSentences(normalized);
                }

                element = walker.GetParent(element);
            }
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            // Context assistance is optional. Unsupported or stale UIA providers
            // must leave the normal transcription path unchanged.
        }

        return null;
    }

    private static bool TryGetEditableTextPattern(AutomationElement element, out TextPattern textPattern)
    {
        textPattern = null!;
        try
        {
            var current = element.Current;
            if (current.IsPassword ||
                !element.TryGetCurrentPattern(TextPattern.Pattern, out var textValue) ||
                textValue is not TextPattern candidatePattern ||
                !IsEditableTextProvider(element))
                return false;

            textPattern = candidatePattern;
            return true;
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return false;
        }
    }

    private static bool IsEditableTextProvider(AutomationElement provider)
    {
        var walker = TreeWalker.ControlViewWalker;
        var element = provider;
        for (var depth = 0; element is not null && depth < MaximumFocusedAncestorDepth; depth++)
        {
            var current = element.Current;
            if (current.IsPassword) return false;

            if (current.ControlType == ControlType.Edit)
            {
                return !element.TryGetCurrentPattern(ValuePattern.Pattern, out var editValue) ||
                       editValue is not ValuePattern editValuePattern || !editValuePattern.Current.IsReadOnly;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                value is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly)
                return true;

            // Some browser contenteditable controls expose a focused Document
            // instead of Edit. Only accept it when that Document owns keyboard focus.
            if (current.ControlType == ControlType.Document && current.HasKeyboardFocus &&
                current.IsKeyboardFocusable)
                return true;

            element = walker.GetParent(element);
        }

        return false;
    }

    private static bool IsWithinWindow(AutomationElement element, IntPtr targetWindow)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < MaximumWindowAncestorDepth; depth++)
        {
            var nativeWindowHandle = element.Current.NativeWindowHandle;
            if (nativeWindowHandle != 0 && new IntPtr(nativeWindowHandle) == targetWindow)
                return true;
            element = walker.GetParent(element);
        }

        return false;
    }

    private static string Normalize(string? value) => WhitespaceRegex()
        .Replace((value ?? string.Empty).Replace('\uFFFC', ' ').Replace('\u00A0', ' '), " ")
        .Trim();

    private static string KeepRecentSentences(string text)
    {
        if (text.Length <= MaximumContextCharacters)
            return text;

        var start = text.Length - MaximumContextCharacters;
        var punctuationIndexes = new List<int>();
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] is '。' or '！' or '？' or '.' or '!' or '?')
                punctuationIndexes.Add(index);
        }

        if (punctuationIndexes.Count >= 2)
        {
            var boundaryIndex = Math.Max(0, punctuationIndexes.Count - 3);
            start = punctuationIndexes[boundaryIndex] + 1;
        }
        else
        {
            var firstSpace = text.IndexOf(' ', start);
            if (firstSpace >= start && firstSpace < start + 24)
                start = firstSpace + 1;
        }

        if (start < text.Length && char.IsLowSurrogate(text[start]))
            start++;
        return text[start..].Trim();
    }

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException;

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
