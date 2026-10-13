using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed class SelectionEditContext
{
    public SelectionEditContext(IntPtr targetWindow, int[] providerRuntimeId, string selectedText)
    {
        TargetWindow = targetWindow;
        ProviderRuntimeId = providerRuntimeId;
        CurrentText = selectedText;
    }

    public IntPtr TargetWindow { get; }
    public int[] ProviderRuntimeId { get; }
    public string CurrentText { get; set; }
}

internal readonly record struct SelectionEditResult(bool Applied, bool CanContinue, string Message);

internal static class SelectionEditService
{
    private const int MaximumSelectionLength = 20_000;
    private const int MaximumReplacementLength = 20_000;
    private const int MaximumFocusedAncestors = 12;

    public static bool TryCapture(out SelectionEditContext? context, out string? failureMessage)
    {
        context = null;
        failureMessage = null;
        var targetWindow = PasteService.GetForegroundWindowHandle();
        if (targetWindow == IntPtr.Zero)
            return false;

        try
        {
            var provider = FindFocusedTextProvider(null, out var textPattern);
            if (provider is null || textPattern is null)
                return false;

            var selections = textPattern.GetSelection();
            if (selections is null || selections.Length == 0)
                return false;
            if (selections.Length != 1)
            {
                failureMessage = "複数の選択範囲がある入力欄には対応していません。1つの文章を選択してください。";
                return false;
            }

            var selectedText = NormalizeText(selections[0].GetText(-1));
            if (string.IsNullOrWhiteSpace(selectedText))
                return false;
            if (selectedText.Length > MaximumSelectionLength)
            {
                failureMessage = "選択範囲が長すぎます。20,000文字以内の文章を選択してください。";
                return false;
            }

            context = new SelectionEditContext(targetWindow, provider.GetRuntimeId(), selectedText);
            return true;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            failureMessage = "選択範囲を読み取れませんでした。対応するテキスト入力欄で選択し直してください。";
            return false;
        }
    }

    public static async Task<SelectionEditResult> ReplaceCurrentSelectionAsync(
        SelectionEditContext context,
        string replacement,
        CancellationToken cancellationToken)
    {
        var replacementApplied = false;
        if (string.IsNullOrWhiteSpace(replacement))
            return new SelectionEditResult(false, false, "編集結果が空でした。選択文は変更していません。");
        if (replacement.Length > MaximumReplacementLength)
            return new SelectionEditResult(false, false, "編集結果が長すぎるため、選択文は変更していません。");
        if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
            return new SelectionEditResult(false, false, "編集中に前面のアプリが変わったため、誤貼り付けを防いで編集を終了しました。");

        try
        {
            var provider = FindFocusedTextProvider(context.ProviderRuntimeId, out var textPattern);
            if (provider is null || textPattern is null)
                return new SelectionEditResult(false, false, "編集中に入力欄が変わったため、選択文は変更していません。");

            var selections = textPattern.GetSelection();
            if (selections is null || selections.Length != 1)
                return new SelectionEditResult(false, false, "選択範囲が変わったため、選択文は変更していません。");

            var currentSelection = selections[0];
            var selectedText = NormalizeText(currentSelection.GetText(-1));
            if (!string.Equals(selectedText, context.CurrentText, StringComparison.Ordinal))
                return new SelectionEditResult(false, false, "選択文が編集中に変わったため、選択文は変更していません。");

            currentSelection.Select();
            var paste = PasteService.CopyAndPaste(replacement, context.TargetWindow);
            if (paste != PasteResult.Pasted)
                return new SelectionEditResult(false, false, "対象アプリへ貼り付けできませんでした。選択文は変更していません。");

            replacementApplied = true;
            context.CurrentText = replacement;
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return new SelectionEditResult(true, false, "編集を適用しました。対象アプリが切り替わったため、連続編集を終了しました。");
            if (!await TryReselectInsertedTextAsync(
                    context.ProviderRuntimeId, replacement, context.TargetWindow, cancellationToken))
                return new SelectionEditResult(true, false, "編集を適用しました。入力欄が選択状態を維持できないため、連続編集を終了しました。");

            return new SelectionEditResult(true, true, "選択文を編集しました。続けて話してください。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return replacementApplied
                ? new SelectionEditResult(true, false, "編集を適用しましたが、選択状態を安全に維持できません。連続編集を終了しました。")
                : new SelectionEditResult(false, false, "選択範囲を安全に更新できませんでした。 " + ex.GetBaseException().Message);
        }
    }

    private static AutomationElement? FindFocusedTextProvider(int[]? requiredRuntimeId, out TextPattern? pattern)
    {
        pattern = null;
        var element = AutomationElement.FocusedElement;
        var walker = TreeWalker.ControlViewWalker;

        for (var depth = 0; element is not null && depth < MaximumFocusedAncestors; depth++)
        {
            if (element.Current.IsPassword)
                return null;

            if ((requiredRuntimeId is null || RuntimeIdMatches(element, requiredRuntimeId)) &&
                element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                pattern = textPattern;
                return element;
            }

            element = walker.GetParent(element);
        }

        return null;
    }

    private static bool RuntimeIdMatches(AutomationElement element, int[] expected)
    {
        var actual = element.GetRuntimeId();
        return actual is not null && actual.AsSpan().SequenceEqual(expected);
    }

    private static async Task<bool> TryReselectInsertedTextAsync(
        TextPattern textPattern,
        string insertedText,
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        var currentSelections = textPattern.GetSelection();
        if (currentSelections is null || currentSelections.Length != 1)
            return false;

        var currentSelection = currentSelections[0];
        var insertedRange = string.Equals(
            NormalizeText(currentSelection.GetText(-1)),
            NormalizeText(insertedText),
            StringComparison.Ordinal)
            ? currentSelection.Clone()
            : FindInsertedTextRange(textPattern.DocumentRange, currentSelection, insertedText);
        if (insertedRange is null) return false;

        // UIA providers can report a selected range without applying a visible input selection.
        // Put the real caret at the inserted text's end, then select it with native keyboard input.
        var caretRange = insertedRange.Clone();
        caretRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.Start,
            insertedRange,
            TextPatternRangeEndpoint.End);
        caretRange.Select();
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        if (PasteService.GetForegroundWindowHandle() != targetWindow ||
            !PasteService.SelectTextBeforeCaret(insertedText, targetWindow))
            return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            var verified = textPattern.GetSelection();
            if (verified is { Length: 1 } &&
                string.Equals(NormalizeText(verified[0].GetText(-1)), NormalizeText(insertedText), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static TextPatternRange? FindInsertedTextRange(
        TextPatternRange documentRange,
        TextPatternRange currentSelection,
        string insertedText)
    {
        var normalizedTarget = NormalizeText(insertedText);
        var candidates = new[]
        {
            insertedText,
            insertedText.Replace("\n", "\r\n", StringComparison.Ordinal),
            insertedText.Replace("\n", "\r", StringComparison.Ordinal)
        }.Distinct(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var textBeforeCaret = documentRange.Clone();
            textBeforeCaret.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                currentSelection,
                TextPatternRangeEndpoint.Start);
            var matchBeforeCaret = textBeforeCaret.FindText(candidate, backward: true, ignoreCase: false);
            if (matchBeforeCaret is not null && matchBeforeCaret.CompareEndpoints(
                    TextPatternRangeEndpoint.End,
                    currentSelection,
                    TextPatternRangeEndpoint.Start) == 0)
                return matchBeforeCaret;

            var textFromSelectionStart = documentRange.Clone();
            textFromSelectionStart.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                currentSelection,
                TextPatternRangeEndpoint.Start);
            var matchAtSelectionStart = textFromSelectionStart.FindText(candidate, backward: false, ignoreCase: false);
            if (matchAtSelectionStart is not null && matchAtSelectionStart.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    currentSelection,
                    TextPatternRangeEndpoint.Start) == 0)
                return matchAtSelectionStart;
        }

        var matches = new List<TextPatternRange>();
        foreach (var candidate in candidates)
        {
            var searchRange = documentRange.Clone();
            for (var count = 0; count < 256; count++)
            {
                var match = searchRange.FindText(candidate, backward: false, ignoreCase: false);
                if (match is null) break;

                if (string.Equals(NormalizeText(match.GetText(-1)), normalizedTarget, StringComparison.Ordinal) &&
                    !matches.Any(existing => existing.CompareEndpoints(TextPatternRangeEndpoint.Start, match,
                        TextPatternRangeEndpoint.Start) == 0 &&
                        existing.CompareEndpoints(TextPatternRangeEndpoint.End, match,
                            TextPatternRangeEndpoint.End) == 0))
                    matches.Add(match);

                var nextSearchRange = match.Clone();
                nextSearchRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, match, TextPatternRangeEndpoint.End);
                if (nextSearchRange.CompareEndpoints(TextPatternRangeEndpoint.Start, searchRange,
                        TextPatternRangeEndpoint.Start) <= 0)
                    break;
                searchRange = nextSearchRange;
            }
        }

        if (matches.Count == 0) return null;

        // Prefer a match ending at the post-paste caret, then a match beginning at the
        // pre-paste selection's start. These anchors disambiguate repeated text in documents.
        var collapsedSelection = currentSelection.CompareEndpoints(
            TextPatternRangeEndpoint.Start, currentSelection, TextPatternRangeEndpoint.End) == 0;
        if (collapsedSelection)
        {
            var caretMatch = matches.FirstOrDefault(match => match.CompareEndpoints(
                TextPatternRangeEndpoint.End, currentSelection, TextPatternRangeEndpoint.Start) == 0);
            if (caretMatch is not null) return caretMatch;
        }

        var startMatch = matches.FirstOrDefault(match => match.CompareEndpoints(
            TextPatternRangeEndpoint.Start, currentSelection, TextPatternRangeEndpoint.Start) == 0);
        if (startMatch is not null) return startMatch;

        var endMatch = matches.FirstOrDefault(match => match.CompareEndpoints(
            TextPatternRangeEndpoint.End, currentSelection, TextPatternRangeEndpoint.End) == 0);
        if (endMatch is not null) return endMatch;

        return matches.Count == 1 ? matches[0] : null;
    }

    private static async Task<bool> TryReselectInsertedTextAsync(
        int[] providerRuntimeId,
        string insertedText,
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        const int MaximumAttempts = 10;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PasteService.GetForegroundWindowHandle() != targetWindow)
                return false;

            try
            {
                var provider = FindFocusedTextProvider(providerRuntimeId, out var textPattern);
                if (provider is not null && textPattern is not null && await TryReselectInsertedTextAsync(
                        textPattern, insertedText, targetWindow, cancellationToken))
                    return true;
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                // Some text providers update their selection asynchronously after Ctrl+V.
            }

            if (attempt + 1 < MaximumAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
        }

        return false;
    }

    private static string NormalizeText(string? text) => (text ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException;
}
