using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed class SelectionEditContext
{
    private int[] _providerRuntimeId;
    private TextPatternRange _lastKnownSelectionRange;
    private AutomationElement _providerElement;

    public SelectionEditContext(IntPtr targetWindow, AutomationElement providerElement, int[] providerRuntimeId,
        TextPatternRange selectedRange, string selectedText, bool isEditable)
    {
        TargetWindow = targetWindow;
        _providerElement = providerElement;
        _providerRuntimeId = providerRuntimeId.ToArray();
        _lastKnownSelectionRange = selectedRange.Clone();
        CurrentText = selectedText;
        IsEditable = isEditable;
    }

    public IntPtr TargetWindow { get; }
    public int[] ProviderRuntimeId => _providerRuntimeId;
    public AutomationElement ProviderElement => _providerElement;
    public string CurrentText { get; set; }
    public bool IsEditable { get; }

    public void UpdateProvider(AutomationElement provider)
    {
        _providerElement = provider;
        _providerRuntimeId = provider.GetRuntimeId().ToArray();
    }

    public TextPatternRange CloneLastKnownSelectionRange() => _lastKnownSelectionRange.Clone();

    public void UpdateLastKnownSelectionRange(TextPatternRange selectionRange) =>
        _lastKnownSelectionRange = selectionRange.Clone();
}

internal readonly record struct SelectionEditResult(bool Applied, bool CanContinue, string Message);

internal static class SelectionEditService
{
    private const int MaximumSelectionLength = 20_000;
    private const int MaximumReplacementLength = 20_000;
    private const int MaximumFocusedAncestors = 64;
    private const int MaximumWindowAncestorDepth = 256;

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

            context = new SelectionEditContext(targetWindow, provider, provider.GetRuntimeId(), selections[0], selectedText,
                IsEditableTextProvider(provider));
            SelectionEditDiagnosticLog.Write("selection_captured", targetWindow, provider, "matches_original");
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
        for (var depth = 0; element is not null && depth < MaximumWindowAncestorDepth; depth++)
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
        var previousText = context.CurrentText;
        if (string.IsNullOrWhiteSpace(replacement))
            return new SelectionEditResult(false, IsTargetWindowForeground(context), "編集結果が空でした。選択文は変更していません。");
        if (replacement.Length > MaximumReplacementLength)
            return new SelectionEditResult(false, IsTargetWindowForeground(context), "編集結果が長すぎるため、選択文は変更していません。");

        var clipboardSaved = false;
        bool TrySaveOutputToClipboard(out string? failure)
        {
            failure = null;
            if (clipboardSaved) return true;
            try
            {
                PasteService.CopyTextToClipboard(replacement);
                clipboardSaved = true;
                SelectionEditDiagnosticLog.Write("clipboard_saved", context.TargetWindow, selectionState: "not_checked");
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                SelectionEditDiagnosticLog.Write("clipboard_save_failed", context.TargetWindow,
                    selectionState: "not_checked", exceptionType: ex.GetBaseException().GetType().Name);
                return false;
            }
        }

        if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
        {
            if (!TrySaveOutputToClipboard(out var clipboardError))
                return new SelectionEditResult(false, false, "編集結果をクリップボードへ保存できませんでした。 " + clipboardError);
            SelectionEditDiagnosticLog.Write("target_window_not_foreground", context.TargetWindow,
                selectionState: "not_checked");
            return new SelectionEditResult(false, false,
                "前面のアプリが変わったため貼り付けませんでした。編集結果はクリップボードにコピーされています。");
        }

        try
        {
            var provider = FindFocusedEditProvider(context.ProviderElement, context.ProviderRuntimeId,
                context.TargetWindow, out var textPattern, out var matchedOriginalProvider);
            if (provider is null || textPattern is null)
            {
                SelectionEditDiagnosticLog.Write("editor_not_found", context.TargetWindow,
                    selectionState: "not_checked");
                if (!TrySaveOutputToClipboard(out var clipboardError))
                    return new SelectionEditResult(false, false, "編集結果をクリップボードへ保存できませんでした。 " + clipboardError);
                return new SelectionEditResult(false, false,
                    "入力欄を再確認できませんでした。編集結果はクリップボードにコピーされています。選択し直してから再開してください。");
            }
            SelectionEditDiagnosticLog.Write("editor_found", context.TargetWindow, provider,
                "not_checked", matchedOriginalProvider: matchedOriginalProvider);

            var selections = textPattern.GetSelection();
            TextPatternRange currentSelection = null!;
            var sourceSelectionIsCurrent = selections is { Length: 1 } &&
                string.Equals(NormalizeText(selections[0].GetText(-1)), context.CurrentText, StringComparison.Ordinal);
            if (sourceSelectionIsCurrent)
                currentSelection = selections![0];
            else
            {
                var restored = matchedOriginalProvider && TryRestoreLastKnownSelection(
                    context, textPattern, out currentSelection);
                SelectionEditDiagnosticLog.Write(restored ? "selection_restored" : "selection_mismatch",
                    context.TargetWindow, provider, restored ? "matches_original" : "other",
                    matchedOriginalProvider: matchedOriginalProvider);
                if (!restored)
                {
                    if (!TrySaveOutputToClipboard(out var clipboardError))
                        return new SelectionEditResult(false, false, "編集結果をクリップボードへ保存できませんでした。 " + clipboardError);
                    return new SelectionEditResult(false, false,
                        "元の選択範囲を確認できません。編集結果はクリップボードにコピーされています。選択し直してから再開してください。");
                }
            }

            context.UpdateProvider(provider);
            context.UpdateLastKnownSelectionRange(currentSelection);
            SelectionEditDiagnosticLog.Write("source_selection_verified", context.TargetWindow, provider,
                "matches_original", matchedOriginalProvider: matchedOriginalProvider);
            // Read the selected editor and retain its TextPattern before clipboard
            // access. Some Chromium providers stop appearing in FocusedElement after
            // Clipboard.SetText even though their original text range is still valid.
            if (!TrySaveOutputToClipboard(out var clipboardFailure))
                return new SelectionEditResult(false, false,
                    "編集結果をクリップボードへ保存できませんでした。 " + clipboardFailure);

            if (!await WaitForSelectionTextAsync(context, provider, textPattern, context.CurrentText, cancellationToken))
            {
                var restored = matchedOriginalProvider && TryRestoreLastKnownSelection(
                    context, textPattern, out currentSelection);
                var selectionReady = restored && await WaitForSelectionTextAsync(
                    context, provider, textPattern, context.CurrentText, cancellationToken);
                SelectionEditDiagnosticLog.Write(selectionReady ? "selection_restored_after_clipboard" : "source_selection_lost_before_paste",
                    context.TargetWindow, provider, selectionReady ? "matches_original" : "other",
                    matchedOriginalProvider: matchedOriginalProvider);
                if (!selectionReady)
                    return new SelectionEditResult(false, false,
                        "貼り付け前に元の選択範囲を確認できません。編集結果はクリップボードにコピーされています。選択し直してから再開してください。");
            }

            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
            {
                SelectionEditDiagnosticLog.Write("target_window_changed_before_paste", context.TargetWindow, provider,
                    "not_checked", matchedOriginalProvider: matchedOriginalProvider);
                return new SelectionEditResult(false, false,
                    "前面のアプリが変わったため貼り付けませんでした。編集結果はクリップボードにコピーされています。");
            }

            var pasteAttempt = PasteService.PasteCopiedTextWithDetails(context.TargetWindow);
            SelectionEditDiagnosticLog.Write("paste_input_sent", context.TargetWindow, provider,
                "matches_original", pasteAttempt.EventsSent, pasteAttempt.EventsRequested,
                pasteAttempt.LastErrorCode, matchedOriginalProvider);
            if (pasteAttempt.Result != PasteResult.Pasted)
            {
                var canContinue = IsTargetWindowForeground(context) &&
                                  await WaitForSelectionTextAsync(context, provider, textPattern,
                                      context.CurrentText, cancellationToken);
                SelectionEditDiagnosticLog.Write("paste_input_incomplete", context.TargetWindow, provider,
                    canContinue ? "matches_original" : "other", pasteAttempt.EventsSent,
                    pasteAttempt.EventsRequested, pasteAttempt.LastErrorCode, matchedOriginalProvider);
                return new SelectionEditResult(false, canContinue,
                    canContinue
                        ? "自動貼り付けを送れませんでした。結果はクリップボードにコピーされています。続けて話してください。"
                        : "対象アプリへの貼り付けを送れませんでした。結果はクリップボードにコピーされています。");
            }

            // Keep cross-process UI Automation completion checks off WPF's UI
            // dispatcher. Chromium providers can block the caller while updating
            // their accessibility tree after a paste.
            var replacementSelected = await Task.Run(
                () => TryReselectInsertedTextAsync(context, provider, textPattern, replacement, cancellationToken),
                cancellationToken);
            if (replacementSelected)
            {
                context.CurrentText = replacement;
                SelectionEditDiagnosticLog.Write("replacement_verified_and_selected", context.TargetWindow,
                    selectionState: "matches_replacement", inputEventsSent: pasteAttempt.EventsSent,
                    inputEventsRequested: pasteAttempt.EventsRequested,
                    inputLastErrorCode: pasteAttempt.LastErrorCode,
                    matchedOriginalProvider: matchedOriginalProvider);
                return new SelectionEditResult(true, true, "選択文を編集しました。続けて話してください。");
            }

            var originalSelectionRemains = IsTargetWindowForeground(context) && await Task.Run(
                () => WaitForSelectionTextAsync(context, provider, textPattern, previousText, cancellationToken),
                cancellationToken);
            if (originalSelectionRemains)
            {
                SelectionEditDiagnosticLog.Write("replacement_not_applied", context.TargetWindow, provider,
                    "matches_original", pasteAttempt.EventsSent, pasteAttempt.EventsRequested,
                    pasteAttempt.LastErrorCode, matchedOriginalProvider);
                return new SelectionEditResult(false, true,
                    "貼り付け後に選択文が変わりませんでした。編集結果はクリップボードにコピーされています。続けて話してください。");
            }

            SelectionEditDiagnosticLog.Write("replacement_unconfirmed", context.TargetWindow, provider,
                "other", pasteAttempt.EventsSent, pasteAttempt.EventsRequested,
                pasteAttempt.LastErrorCode, matchedOriginalProvider);
            return new SelectionEditResult(false, false,
                "貼り付け後の文章変更を確認できません。編集結果はクリップボードにコピーされています。選択し直してから再開してください。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!clipboardSaved)
                TrySaveOutputToClipboard(out _);
            SelectionEditDiagnosticLog.Write("replacement_exception", context.TargetWindow,
                selectionState: "unknown", exceptionType: ex.GetBaseException().GetType().Name);
            return new SelectionEditResult(false, false,
                (clipboardSaved
                    ? "選択範囲を安全に更新できませんでした。編集結果はクリップボードにコピーされています。 "
                    : "選択範囲を安全に更新できませんでした。編集結果をクリップボードへ保存できませんでした。 ") +
                ex.GetBaseException().Message);
        }
    }

    private static bool TryRestoreLastKnownSelection(
        SelectionEditContext context,
        TextPattern textPattern,
        out TextPatternRange selection)
    {
        selection = null!;
        var savedRange = context.CloneLastKnownSelectionRange();
        if (!string.Equals(NormalizeText(savedRange.GetText(-1)), context.CurrentText, StringComparison.Ordinal))
            return false;

        savedRange.Select();
        var restoredSelections = textPattern.GetSelection();
        if (restoredSelections is not { Length: 1 } ||
            !string.Equals(NormalizeText(restoredSelections[0].GetText(-1)), context.CurrentText,
                StringComparison.Ordinal))
            return false;

        selection = restoredSelections[0];
        context.UpdateLastKnownSelectionRange(selection);
        return true;
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
                var provider = FindFocusedEditProvider(context.ProviderElement, context.ProviderRuntimeId,
                    context.TargetWindow, out var textPattern, out _);
                if (provider is not null && textPattern is not null)
                {
                    var selections = textPattern.GetSelection();
                    if (selections is { Length: 1 })
                    {
                        TextPatternRange? targetRange = null;
                        if (string.Equals(NormalizeText(selections[0].GetText(-1)), NormalizeText(expectedText),
                                StringComparison.Ordinal))
                            targetRange = selections[0].Clone();
                        else
                            TryGetInsertedTextRange(textPattern, expectedText, out targetRange);

                        if (targetRange is not null)
                        {
                            targetRange.Select();
                            if (IsReplacementSelected(textPattern, expectedText))
                            {
                                context.UpdateProvider(provider);
                                context.UpdateLastKnownSelectionRange(targetRange);
                                return true;
                            }
                        }

                        if (IsReplacementSelected(textPattern, expectedText))
                        {
                            context.UpdateProvider(provider);
                            context.UpdateLastKnownSelectionRange(selections[0]);
                            return true;
                        }
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
        AutomationElement provider,
        TextPattern textPattern,
        string expectedText,
        CancellationToken cancellationToken)
    {
        const int MaximumAttempts = 8;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return false;

            try
            {
                var selections = textPattern.GetSelection();
                if (selections is { Length: 1 } &&
                    string.Equals(NormalizeText(selections[0].GetText(-1)), NormalizeText(expectedText),
                        StringComparison.Ordinal))
                {
                    context.UpdateProvider(provider);
                    context.UpdateLastKnownSelectionRange(selections[0]);
                    return true;
                }
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
        AutomationElement? retainedProvider,
        int[]? requiredRuntimeId,
        IntPtr targetWindow,
        out TextPattern? pattern,
        out bool matchedRequiredRuntimeId)
    {
        pattern = null;
        matchedRequiredRuntimeId = false;
        if (retainedProvider is not null)
        {
            try
            {
                if (IsWithinWindow(retainedProvider, targetWindow) && IsEditableTextProvider(retainedProvider) &&
                    retainedProvider.TryGetCurrentPattern(TextPattern.Pattern, out var retainedPatternObject) &&
                    retainedPatternObject is TextPattern retainedPattern)
                {
                    pattern = retainedPattern;
                    matchedRequiredRuntimeId = true;
                    return retainedProvider;
                }
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                // Try reacquiring the same provider by runtime ID and focus path.
            }
        }

        var provider = requiredRuntimeId is null
            ? null
            : FindFocusedTextProvider(requiredRuntimeId, targetWindow, out pattern);
        if (provider is null && requiredRuntimeId is not null)
            provider = FindTextProviderByRuntimeIdInWindow(requiredRuntimeId, targetWindow, out pattern);
        if (provider is not null)
        {
            matchedRequiredRuntimeId = true;
            return provider;
        }

        provider = FindFocusedTextProvider(null, targetWindow, out pattern);
        return provider is not null && IsEditableTextProvider(provider) ? provider : null;
    }

    private static AutomationElement? FindTextProviderByRuntimeIdInWindow(
        int[] requiredRuntimeId,
        IntPtr targetWindow,
        out TextPattern? pattern)
    {
        pattern = null;
        try
        {
            var root = AutomationElement.FromHandle(targetWindow);
            var candidates = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            for (var index = 0; index < candidates.Count && index < 4096; index++)
            {
                var candidate = candidates[index];
                if (!RuntimeIdMatches(candidate, requiredRuntimeId) || !IsEditableTextProvider(candidate) ||
                    !candidate.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                    patternObject is not TextPattern textPattern)
                    continue;

                pattern = textPattern;
                return candidate;
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            // The focused chain and the saved UIA provider remain the primary paths.
        }

        return null;
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

        for (var depth = 0; element is not null && depth < MaximumWindowAncestorDepth; depth++)
        {
            if (element.Current.IsPassword)
                return null;

            if ((requiredRuntimeId is null || RuntimeIdMatches(element, requiredRuntimeId)) &&
                element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern &&
                IsEditableTextProvider(element))
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

    private static bool TryGetInsertedTextRangeAtCaret(
        TextPattern textPattern,
        string insertedText,
        out TextPatternRange? insertedRange)
    {
        insertedRange = null;
        var selections = textPattern.GetSelection();
        if (selections is not { Length: 1 })
            return false;

        var currentSelection = selections[0];
        var normalizedTarget = NormalizeText(insertedText);
        if (string.Equals(NormalizeText(currentSelection.GetText(-1)), normalizedTarget, StringComparison.Ordinal))
        {
            insertedRange = currentSelection.Clone();
            return true;
        }

        if (currentSelection.CompareEndpoints(TextPatternRangeEndpoint.Start, currentSelection,
                TextPatternRangeEndpoint.End) != 0)
            return false;

        // After Ctrl+V most editors leave a collapsed caret at the end of the
        // inserted text. Build a small range backwards from that caret instead of
        // searching the entire document, which can be slow or block on web providers.
        var counts = new HashSet<int>();
        AddNearbyCharacterCounts(counts, normalizedTarget.Length);
        AddNearbyCharacterCounts(counts, new StringInfo(normalizedTarget).LengthInTextElements);
        foreach (var characterCount in counts.Order())
        {
            if (characterCount <= 0) continue;
            var candidate = currentSelection.Clone();
            var moved = candidate.MoveEndpointByUnit(
                TextPatternRangeEndpoint.Start,
                TextUnit.Character,
                -characterCount);
            if (moved == 0)
                continue;

            if (string.Equals(NormalizeText(candidate.GetText(-1)), normalizedTarget, StringComparison.Ordinal))
            {
                insertedRange = candidate;
                return true;
            }
        }

        return false;
    }

    private static void AddNearbyCharacterCounts(HashSet<int> counts, int center)
    {
        for (var adjustment = -2; adjustment <= 2; adjustment++)
        {
            var count = center + adjustment;
            if (count > 0) counts.Add(count);
        }
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
                nextSearchRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, documentRange, TextPatternRangeEndpoint.End);
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

        // A unique match elsewhere in the document does not prove that this paste
        // replaced the selected range. Require a caret or selection-boundary anchor.
        return null;
    }

    private static async Task<bool> TryReselectInsertedTextAsync(
        SelectionEditContext context,
        AutomationElement initialProvider,
        TextPattern initialTextPattern,
        string insertedText,
        CancellationToken cancellationToken)
    {
        const int MaximumAttempts = 10;
        var searchProvider = initialProvider;
        var searchPattern = initialTextPattern;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                return false;

            AutomationElement? provider = searchProvider;
            TextPattern? textPattern = searchPattern;
            TextPatternRange? insertedRange = null;
            var matchedOriginalProvider = true;
            try
            {
                if (textPattern is not null && !TryGetInsertedTextRangeAtCaret(textPattern, insertedText, out insertedRange))
                    insertedRange = null;
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                insertedRange = null;
            }

            if (insertedRange is null)
            {
                try
                {
                    var rebound = FindFocusedEditProvider(context.ProviderElement, context.ProviderRuntimeId,
                        context.TargetWindow, out var reboundPattern, out matchedOriginalProvider);
                    if (rebound is not null && reboundPattern is not null)
                    {
                        provider = searchProvider = rebound;
                        textPattern = searchPattern = reboundPattern;
                        TryGetInsertedTextRangeAtCaret(textPattern, insertedText, out insertedRange);
                    }
                }
                catch (Exception ex) when (IsAutomationFailure(ex))
                {
                    insertedRange = null;
                }
            }

            if (insertedRange is not null && provider is not null && textPattern is not null)
            {
                try
                {
                    if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                        return false;

                    // Select the whole replacement in one UI Automation operation.
                    // Polling below only verifies that the target app kept this selection.
                    insertedRange!.Select();
                    if (IsReplacementSelected(textPattern, insertedText))
                    {
                        context.UpdateProvider(provider);
                        context.UpdateLastKnownSelectionRange(insertedRange);
                        SelectionEditDiagnosticLog.Write("replacement_selection_verified", context.TargetWindow,
                            provider, "matches_replacement", matchedOriginalProvider: matchedOriginalProvider);
                        return true;
                    }

                    for (var verifyAttempt = 0; verifyAttempt < 5; verifyAttempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                        if (PasteService.GetForegroundWindowHandle() != context.TargetWindow)
                            return false;

                        if (IsReplacementSelected(textPattern, insertedText))
                        {
                            context.UpdateProvider(provider);
                            context.UpdateLastKnownSelectionRange(insertedRange);
                            SelectionEditDiagnosticLog.Write("replacement_selection_verified", context.TargetWindow,
                                provider, "matches_replacement", matchedOriginalProvider: matchedOriginalProvider);
                            return true;
                        }
                    }

                    SelectionEditDiagnosticLog.Write("replacement_selection_not_verified", context.TargetWindow,
                        provider, "other", matchedOriginalProvider: matchedOriginalProvider);

                    return false;
                }
                catch (Exception ex) when (IsAutomationFailure(ex))
                {
                    // Some text providers update their document and selection asynchronously.
                }
            }

            SelectionEditDiagnosticLog.Write("replacement_text_not_found", context.TargetWindow, provider,
                "other", matchedOriginalProvider: matchedOriginalProvider);

            if (attempt + 1 < MaximumAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
        }

        return false;
    }

    private static bool IsTargetWindowForeground(SelectionEditContext context) =>
        PasteService.GetForegroundWindowHandle() == context.TargetWindow;

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
