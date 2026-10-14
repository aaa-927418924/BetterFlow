using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed class SelectionEditContext
{
    public SelectionEditContext(IntPtr targetWindow, int[] providerRuntimeId, string selectedText, bool isEditable)
    {
        TargetWindow = targetWindow;
        ProviderRuntimeId = providerRuntimeId;
        CurrentText = selectedText;
        IsEditable = isEditable;
    }

    public IntPtr TargetWindow { get; }
    public int[] ProviderRuntimeId { get; }
    public string CurrentText { get; set; }
    public bool IsEditable { get; }
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
            var provider = FindSelectedTextProvider(targetWindow, out var textPattern, out var selections,
                out var ambiguousSelection);
            if (provider is null || textPattern is null || selections is null)
            {
                if (ambiguousSelection)
                    failureMessage = "選択範囲を一意に特定できませんでした。文章を選択し直してください。";
                return false;
            }

            if (selections.Length != 1)
            {
                failureMessage = "複数の選択範囲には対応していません。1つの文章を選択してください。";
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

            context = new SelectionEditContext(targetWindow, provider.GetRuntimeId(), selectedText,
                IsEditableTextProvider(provider));
            return true;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            failureMessage = "選択範囲を読み取れませんでした。文章を選択し直してください。";
            return false;
        }
    }

    private static AutomationElement? FindSelectedTextProvider(
        IntPtr targetWindow,
        out TextPattern? pattern,
        out TextPatternRange[]? selections,
        out bool ambiguousSelection)
    {
        pattern = null;
        selections = null;
        ambiguousSelection = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var focused = AutomationElement.FocusedElement;
        var walker = TreeWalker.ControlViewWalker;

        // Global hotkey callbacks can observe a stale UIA focused element from the
        // previous foreground app. Only trust the focus chain when it belongs to
        // the window whose selection we are about to capture.
        for (var depth = 0; focused is not null && depth < MaximumFocusedAncestors &&
             IsWithinWindow(focused, targetWindow); depth++)
        {
            if (TryReadSelection(focused, visited, out pattern, out selections))
                return focused;
            focused = walker.GetParent(focused);
        }

        var root = AutomationElement.FromHandle(targetWindow);
        var condition = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        var candidates = root.FindAll(TreeScope.Descendants, condition);
        AutomationElement? fallbackProvider = null;
        TextPattern? fallbackPattern = null;
        TextPatternRange[]? fallbackSelections = null;
        string? fallbackText = null;
        for (var index = 0; index < candidates.Count && index < 4096; index++)
        {
            var candidate = candidates[index];
            if (!TryReadSelection(candidate, visited, out var candidatePattern, out var candidateSelections))
                continue;

            if (candidate.Current.HasKeyboardFocus)
            {
                pattern = candidatePattern;
                selections = candidateSelections;
                return candidate;
            }

            string candidateText;
            try
            {
                candidateText = string.Join("\u001f", candidateSelections!.Select(range => NormalizeText(range.GetText(-1))));
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(candidateText)) continue;

            fallbackProvider ??= candidate;
            if (fallbackPattern is null)
            {
                fallbackPattern = candidatePattern;
                fallbackSelections = candidateSelections;
                fallbackText = candidateText;
            }
            else if (!string.Equals(fallbackText, candidateText, StringComparison.Ordinal))
            {
                ambiguousSelection = true;
            }
        }

        if (ambiguousSelection)
        {
            pattern = null;
            selections = null;
            return null;
        }
        pattern = fallbackPattern;
        selections = fallbackSelections;
        return fallbackProvider;
    }

    private static bool IsWithinWindow(AutomationElement element, IntPtr targetWindow)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < MaximumFocusedAncestors; depth++)
        {
            try
            {
                if (element.Current.NativeWindowHandle != 0 &&
                    new IntPtr(element.Current.NativeWindowHandle) == targetWindow)
                    return true;
                element = walker.GetParent(element);
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryReadSelection(
        AutomationElement element,
        HashSet<string> visited,
        out TextPattern? pattern,
        out TextPatternRange[]? selections)
    {
        pattern = null;
        selections = null;
        try
        {
            if (element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var value) ||
                value is not TextPattern textPattern)
                return false;

            var runtimeId = element.GetRuntimeId();
            var key = runtimeId is null ? string.Empty : string.Join(',', runtimeId);
            if (key.Length > 0 && !visited.Add(key)) return false;

            var selectedRanges = textPattern.GetSelection();
            if (selectedRanges is null || selectedRanges.Length == 0) return false;
            pattern = textPattern;
            selections = selectedRanges;
            return true;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return false;
        }
    }

    private static bool IsEditableTextProvider(AutomationElement provider)
    {
        var walker = TreeWalker.ControlViewWalker;
        var element = provider;
        for (var depth = 0; element is not null && depth < MaximumFocusedAncestors; depth++)
        {
            if (element.Current.IsPassword) return false;
            if (element.Current.ControlType == ControlType.Edit)
            {
                return !element.TryGetCurrentPattern(ValuePattern.Pattern, out var editValue) ||
                       editValue is not ValuePattern editValuePattern || !editValuePattern.Current.IsReadOnly;
            }
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
                return true;
            element = walker.GetParent(element);
        }
        return false;
    }

    public static async Task<SelectionEditResult> ReplaceCurrentSelectionAsync(
        SelectionEditContext context,
        string replacement,
        CancellationToken cancellationToken)
    {
        var replacementApplied = false;
        var previousText = context.CurrentText;
        if (string.IsNullOrWhiteSpace(replacement))
            return new SelectionEditResult(false, false, "編集結果が空でした。選択文は変更していません。");
        if (replacement.Length > MaximumReplacementLength)
            return new SelectionEditResult(false, false, "編集結果が長すぎるため、選択文は変更していません。");
        if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
            return new SelectionEditResult(false, false, "編集中に前面のアプリが変わったため、誤貼り付けを防いで編集を終了しました。");

        try
        {
            var provider = FindFocusedTextProvider(context.ProviderRuntimeId, context.TargetWindow, out var textPattern);
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
            if (!await WaitForSelectionTextAsync(context, context.CurrentText, cancellationToken))
                return new SelectionEditResult(false, false, "貼り付け前に選択範囲を確認できませんでした。選択文は変更していません。");

            var paste = PasteService.CopyAndPaste(replacement, context.TargetWindow);
            if (paste != PasteResult.Pasted)
            {
                var canContinue = await WaitForSelectionTextAsync(context, context.CurrentText, cancellationToken);
                return new SelectionEditResult(false, canContinue,
                    canContinue
                        ? "自動貼り付けを送れませんでした。結果はクリップボードにコピーされています。続けて話してください。"
                        : "対象アプリへの貼り付けを確認できませんでした。選択文は変更していません。");
            }

            replacementApplied = true;
            context.CurrentText = replacement;
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return new SelectionEditResult(true, false, "編集を適用しました。対象アプリが切り替わったため、連続編集を終了しました。");
            if (await TryReselectInsertedTextAsync(
                    context.ProviderRuntimeId, replacement, context.TargetWindow, cancellationToken))
                return new SelectionEditResult(true, true, "選択文を編集しました。続けて話してください。");

            if (await WaitForSelectionTextAsync(
                    context, previousText, cancellationToken, allowProviderFallback: true))
            {
                context.CurrentText = previousText;
                replacementApplied = false;
                return new SelectionEditResult(false, true,
                    "貼り付け後の変更を確認できませんでした。元の選択文は維持しています。結果はクリップボードにコピーされています。続けて話してください。");
            }

            return new SelectionEditResult(true, false, "編集結果を入力しましたが、選択状態を確認できません。安全のため連続編集を終了しました。");
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

    public static async Task<bool> TryReselectCurrentSelectionAsync(
        SelectionEditContext context,
        string expectedText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedText) ||
            !string.Equals(NormalizeText(context.CurrentText), NormalizeText(expectedText), StringComparison.Ordinal))
            return false;

        const int MaximumAttempts = 8;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return false;

            try
            {
                var provider = FindFocusedEditProvider(context.ProviderRuntimeId, context.TargetWindow, out var textPattern);
                if (provider is not null && textPattern is not null)
                {
                    var selections = textPattern.GetSelection();
                    if (selections is { Length: 1 } &&
                        string.Equals(NormalizeText(selections[0].GetText(-1)), NormalizeText(expectedText),
                            StringComparison.Ordinal))
                    {
                        selections[0].Select();
                        if (IsReplacementSelected(textPattern, expectedText))
                            return true;
                    }
                }
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                // Some providers briefly expose a stale selection after text replacement.
            }

            if (attempt + 1 < MaximumAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        return false;
    }

    private static async Task<bool> WaitForSelectionTextAsync(
        SelectionEditContext context,
        string expectedText,
        CancellationToken cancellationToken,
        bool allowProviderFallback = false)
    {
        const int MaximumAttempts = 8;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return false;

            try
            {
                TextPattern? textPattern;
                var provider = allowProviderFallback
                    ? FindFocusedEditProvider(context.ProviderRuntimeId, context.TargetWindow, out textPattern)
                    : FindFocusedTextProvider(context.ProviderRuntimeId, context.TargetWindow, out textPattern);
                var selections = textPattern?.GetSelection();
                if (provider is not null && selections is { Length: 1 } &&
                    string.Equals(NormalizeText(selections[0].GetText(-1)), NormalizeText(expectedText),
                        StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                // Re-read after the host editor has applied the UI Automation selection.
            }

            if (attempt + 1 < MaximumAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    private static AutomationElement? FindFocusedEditProvider(
        int[]? requiredRuntimeId,
        IntPtr targetWindow,
        out TextPattern? pattern)
    {
        var provider = FindFocusedTextProvider(requiredRuntimeId, targetWindow, out pattern);
        if (provider is not null || requiredRuntimeId is null)
            return provider;

        provider = FindFocusedTextProvider(null, targetWindow, out pattern);
        return provider is not null && IsEditableTextProvider(provider) ? provider : null;
    }

    private static AutomationElement? FindFocusedTextProvider(
        int[]? requiredRuntimeId,
        IntPtr targetWindow,
        out TextPattern? pattern)
    {
        pattern = null;
        var element = AutomationElement.FocusedElement;
        var walker = TreeWalker.ControlViewWalker;
        if (element is null || !IsWithinWindow(element, targetWindow))
            return null;

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

    private static bool TryGetInsertedTextRange(
        TextPattern textPattern,
        string insertedText,
        out TextPatternRange? insertedRange)
    {
        insertedRange = null;
        var currentSelections = textPattern.GetSelection();
        if (currentSelections is null || currentSelections.Length != 1)
            return false;

        var currentSelection = currentSelections[0];
        insertedRange = string.Equals(
            NormalizeText(currentSelection.GetText(-1)),
            NormalizeText(insertedText),
            StringComparison.Ordinal)
            ? currentSelection.Clone()
            : FindInsertedTextRange(textPattern.DocumentRange, currentSelection, insertedText);
        return insertedRange is not null;
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
                var provider = FindFocusedEditProvider(providerRuntimeId, targetWindow, out var textPattern);
                if (provider is not null && textPattern is not null &&
                    TryGetInsertedTextRange(textPattern, insertedText, out var insertedRange))
                {
                    if (PasteService.GetForegroundWindowHandle() != targetWindow)
                        return false;

                    // Select the whole replacement in one UI Automation operation.
                    // Polling below only verifies that the target app kept this selection.
                    insertedRange!.Select();
                    if (IsReplacementSelected(textPattern, insertedText))
                        return true;

                    for (var verifyAttempt = 0; verifyAttempt < 5; verifyAttempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                        if (PasteService.GetForegroundWindowHandle() != targetWindow)
                            return false;

                        if (IsReplacementSelected(textPattern, insertedText))
                            return true;
                    }

                    return false;
                }
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

    private static bool IsReplacementSelected(TextPattern textPattern, string insertedText)
    {
        var selections = textPattern.GetSelection();
        return selections is { Length: 1 } &&
               string.Equals(NormalizeText(selections[0].GetText(-1)),
                   NormalizeText(insertedText), StringComparison.Ordinal);
    }

    private static string NormalizeText(string? text) => (text ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException;
}
