using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed record DictionaryCorrectionSubmission(
    string OriginalText,
    string CorrectedText,
    string MonitorId,
    long CandidateId);

internal sealed class AutoDictionaryLearningMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private const int VkReturn = 0x0D;
    private const int VkShift = 0x10;
    private const int MaxDocumentLength = 24_000;
    private const int MaxMonitorMinutes = 10;
    private const int PastePropagationTimeoutMs = 2_000;
    private const int PastePropagationPollMs = 50;
    private const int SnapshotFreshnessMs = 1_500;
    private const int RecentPreClearSnapshotMs = 500;
    private const int TrackedEditorFocusFreshnessMs = 1_000;

    private readonly IntPtr _targetWindow;
    private readonly string _pastedText;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _submitSignal = new(0, 1);
    private readonly object _snapshotLock = new();
    private readonly ConcurrentQueue<SubmitCandidate> _pendingSubmissions = new();
    private readonly string _monitorId = Guid.NewGuid().ToString("N")[..8];
    private readonly TaskCompletionSource<bool> _readySignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LowLevelKeyboardProc _keyboardProc;
    private IntPtr _keyboardHook;
    private Task? _monitorTask;
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private string _originalSegment = string.Empty;
    private string? _editorRuntimeId;
    private EditorSnapshot _snapshot = EditorSnapshot.Unreadable;
    private EditorSnapshot _lastValidSnapshot = EditorSnapshot.Unreadable;
    private SubmitCandidate? _preparedApplicationSubmit;
    private long _snapshotVersion;
    private long _nextCandidateId;
    private long _latestCandidateId;
    private int _enterKeyDown;
    private long _lastSignalSnapshotVersion = -1;
    private long _lastSignalTimestamp;
    private string _lastSignalSource = string.Empty;
    private int _submissionCompleted;
    private int _deduplicatedSignalCount;
    private int _initialized;
    private int _disposed;
    private long _trackedEditorFocusTimestamp;

    private AutoDictionaryLearningMonitor(IntPtr targetWindow, string pastedText)
    {
        _targetWindow = targetWindow;
        _pastedText = NormalizeLineEndings(pastedText);
        _token = _cancellation.Token;
        _keyboardProc = KeyboardHookCallback;
    }

    public event EventHandler<DictionaryCorrectionSubmission>? SubmissionConfirmed;
    public event Action<string>? StatusChanged;

    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _readySignal.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void NotifyApplicationSubmit()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SubmitCandidate? candidate;
        var superseded = false;
        lock (_snapshotLock)
        {
            candidate = _preparedApplicationSubmit;
            _preparedApplicationSubmit = null;
            if (candidate is not null && _submissionCompleted == 0 && candidate.Id >= _latestCandidateId)
                _latestCandidateId = candidate.Id;
            else
            {
                superseded = candidate is not null;
                candidate = null;
            }
        }

        if (candidate is null)
        {
            Log(superseded ? "application_submit_superseded" : "application_submit_unprepared",
                superseded ? "newer-user-submit-candidate-exists" : "send-was-not-snapshotted");
            return;
        }

        EnqueueSubmission(candidate with { Source = "application-send" });
    }

    public bool PrepareApplicationSubmit()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _initialized) == 0)
            return false;

        SubmitCandidate candidate;
        var valid = false;
        lock (_snapshotLock)
        {
            _preparedApplicationSubmit = null;
            candidate = CreateCandidateLocked("application-send", DateTimeOffset.UtcNow);
            valid = candidate.IsValid;
            if (valid) _preparedApplicationSubmit = candidate;
        }

        if (!valid)
        {
            Log("application_submit_prepare_failed",
                $"{candidate.Snapshot.State};{SnapshotDescription(candidate.Snapshot)}", 0, candidate.Id);
            return false;
        }

        Log("application_submit_prepared",
            $"{SnapshotDescription(candidate.Snapshot)};signal-utc={candidate.SignalTime:O}", 0, candidate.Id);
        return true;
    }

    public void CancelPreparedApplicationSubmit()
    {
        SubmitCandidate? candidate;
        lock (_snapshotLock)
        {
            candidate = _preparedApplicationSubmit;
            _preparedApplicationSubmit = null;
        }

        if (candidate is not null)
            Log("application_submit_cancelled",
                $"send-input-failed;{SnapshotDescription(candidate.Snapshot)}", 0, candidate.Id);
    }

    public static AutoDictionaryLearningMonitor? TryStart(IntPtr targetWindow, string pastedText)
    {
        if (targetWindow == IntPtr.Zero || string.IsNullOrWhiteSpace(pastedText) ||
            pastedText.Length > MaxDocumentLength || PasteService.GetForegroundWindowHandle() != targetWindow)
        {
            DictionaryLearningDiagnosticLog.Write("monitor_not_started", "invalid-target-or-paste");
            return null;
        }

        var monitor = new AutoDictionaryLearningMonitor(targetWindow, pastedText);
        var module = GetModuleHandle(null);
        monitor._keyboardHook = SetWindowsHookEx(WhKeyboardLl, monitor._keyboardProc, module, 0);
        if (monitor._keyboardHook == IntPtr.Zero)
        {
            monitor.Log("monitor_not_started", "keyboard-hook-failed", Marshal.GetLastWin32Error());
            monitor.Dispose();
            return null;
        }

        monitor.Log("monitor_started");
        monitor._monitorTask = Task.Run(monitor.MonitorAsync);
        return monitor;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        lock (_snapshotLock)
        {
            _prefix = string.Empty;
            _suffix = string.Empty;
            _originalSegment = string.Empty;
            _snapshot = EditorSnapshot.Unreadable;
            _lastValidSnapshot = EditorSnapshot.Unreadable;
            _preparedApplicationSubmit = null;
        }
        while (_pendingSubmissions.TryDequeue(out _)) { }
        _submitSignal.Dispose();
        _cancellation.Dispose();
    }

    private async Task MonitorAsync()
    {
        AutomationElement? editor = null;
        TextPattern? textPattern = null;
        ValuePattern? valuePattern = null;
        AutomationElement? root = null;
        AutomationEventHandler? invokedHandler = null;
        AutomationEventHandler? textChangedHandler = null;
        AutomationPropertyChangedEventHandler? valueChangedHandler = null;
        var editorEventsAttached = false;
        var token = _token;

        void AttachEditorEvents(AutomationElement target)
        {
            textChangedHandler ??= (_, _) =>
            {
                var currentEditor = editor;
                if (currentEditor is not null)
                    RefreshSnapshot(currentEditor, textPattern, valuePattern);
            };
            valueChangedHandler ??= (_, _) =>
            {
                var currentEditor = editor;
                if (currentEditor is not null)
                    RefreshSnapshot(currentEditor, textPattern, valuePattern);
            };
            try { Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, target,
                TreeScope.Element, textChangedHandler); }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            if (valuePattern is not null)
            {
                try { Automation.AddAutomationPropertyChangedEventHandler(target, TreeScope.Element,
                    valueChangedHandler, ValuePattern.ValueProperty); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
        }

        void DetachEditorEvents(AutomationElement target)
        {
            if (textChangedHandler is not null)
            {
                try { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, target, textChangedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (valueChangedHandler is not null)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(target, valueChangedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
        }

        bool TryRebindEditor()
        {
            if (editor is null || string.IsNullOrWhiteSpace(_editorRuntimeId))
            {
                Log("editor_rebind_rejected", "runtime-id-unavailable");
                return false;
            }

            var replacement = FindFocusedEditor(_targetWindow, out var replacementTextPattern,
                out var replacementValuePattern);
            if (replacement is null)
            {
                Log("editor_rebind_failed", "focused-editor-not-found");
                return false;
            }

            var replacementRuntimeId = GetRuntimeId(replacement);
            if (!string.Equals(_editorRuntimeId, replacementRuntimeId, StringComparison.Ordinal))
            {
                Log("editor_rebind_rejected", replacementRuntimeId is null
                    ? "replacement-runtime-id-unavailable"
                    : "runtime-id-mismatch");
                return false;
            }

            var previousEditor = editor;
            if (editorEventsAttached) DetachEditorEvents(previousEditor);
            editor = replacement;
            textPattern = replacementTextPattern;
            valuePattern = replacementValuePattern;
            if (editorEventsAttached) AttachEditorEvents(replacement);
            Log("editor_rebound", "same-runtime-id");
            return true;
        }

        bool TryReadCurrentEditor(out string text, out string readState)
        {
            text = string.Empty;
            readState = "editor-not-bound";
            if (editor is null) return false;
            if (TryReadText(editor, textPattern, valuePattern, out text, out readState)) return true;
            if (!IsRecoverableEditorFailure(readState) || !TryRebindEditor()) return false;
            var reboundReadState = "editor-not-bound";
            if (editor is not null && TryReadText(editor, textPattern, valuePattern, out text, out reboundReadState))
            {
                readState = $"rebound-same-runtime-id;{reboundReadState}";
                return true;
            }
            readState = $"rebind-read-failed:{reboundReadState}";
            return false;
        }

        (AutomationElement Element, TextPattern? TextPattern, ValuePattern? ValuePattern) GetCurrentEditorAccess() =>
            (editor!, textPattern, valuePattern);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(MaxMonitorMinutes));
            var monitorToken = timeout.Token;

            for (var attempt = 0; attempt < 15 && editor is null; attempt++)
            {
                monitorToken.ThrowIfCancellationRequested();
                editor = FindFocusedEditor(_targetWindow, out textPattern, out valuePattern);
                if (editor is null)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), monitorToken).ConfigureAwait(false);
            }

            if (editor is null)
            {
                ReportStatus("editor_not_found", "入力欄を読み取れず、自動登録の監視を開始できませんでした。");
                return;
            }
            _editorRuntimeId = GetRuntimeId(editor);
            Log("editor_bound", _editorRuntimeId is null ? "runtime-id-unavailable" : "runtime-id-captured");
            var initialText = string.Empty;
            var insertedStart = -1;
            var pasteReadAttempts = 0;
            var pasteWait = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                monitorToken.ThrowIfCancellationRequested();
                pasteReadAttempts++;
                if (TryReadCurrentEditor(out var currentText, out _))
                {
                    initialText = NormalizeLineEndings(currentText);
                    if (initialText.Length > MaxDocumentLength)
                    {
                        ReportStatus("editor_text_too_long", "入力欄が長いため、自動登録の監視を開始できませんでした。");
                        return;
                    }

                    insertedStart = initialText.IndexOf(_pastedText, StringComparison.Ordinal);
                    if (insertedStart >= 0) break;
                }

                if (pasteWait.ElapsedMilliseconds >= PastePropagationTimeoutMs) break;
                await Task.Delay(TimeSpan.FromMilliseconds(PastePropagationPollMs), monitorToken).ConfigureAwait(false);
            } while (true);

            if (insertedStart < 0)
            {
                Log("paste_wait_expired", "text-not-visible", pasteReadAttempts);
                ReportStatus("pasted_text_not_found", "入力欄に貼り付けた文章を確認できず、自動登録の監視を開始できませんでした。");
                return;
            }
            Log("pasted_text_detected",
                pasteReadAttempts == 1 ? "immediate" : "after-ui-update", pasteReadAttempts);
            if (initialText.IndexOf(_pastedText, insertedStart + _pastedText.Length,
                    StringComparison.Ordinal) >= 0)
            {
                ReportStatus("pasted_text_ambiguous", "同じ文章が入力欄に複数あり、追跡を開始できませんでした。");
                return;
            }

            lock (_snapshotLock)
            {
                _prefix = initialText[..insertedStart];
                _suffix = initialText[(insertedStart + _pastedText.Length)..];
                _originalSegment = _pastedText;
                SetSnapshotLocked(initialText, _pastedText, SnapshotState.Valid, "initial-paste");
            }
            Volatile.Write(ref _initialized, 1);
            UpdateTrackedEditorFocus(editor);
            ReportStatus("tracking_started", "音声入力した文章を送信するまで監視しています。");

            editorEventsAttached = true;
            AttachEditorEvents(editor);

            try
            {
                root = AutomationElement.FromHandle(_targetWindow);
                invokedHandler = OnAutomationEvent;
                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, root,
                    TreeScope.Descendants, invokedHandler);
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { }

            _readySignal.TrySetResult(true);

            while (!monitorToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), monitorToken).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0) return;

                while (_submitSignal.Wait(0)) { }
                var deduplicatedCount = Interlocked.Exchange(ref _deduplicatedSignalCount, 0);
                if (deduplicatedCount > 0)
                    Log("submit_signal_deduplicated", "repeat-or-cross-source", deduplicatedCount);
                if (TryReadCurrentEditor(out var currentText, out var readState))
                {
                    UpdateTrackedEditorFocus(editor);
                    currentText = NormalizeLineEndings(currentText);
                    if (currentText.Length > MaxDocumentLength)
                    {
                        ReportStatus("editor_text_too_long", "入力欄が長くなったため、自動登録の監視を終了しました。");
                        return;
                    }
                    if (TryExtractSegment(currentText, out var segment))
                    {
                        SetSnapshot(currentText, segment);
                    }
                    else
                    {
                        var changed = SetSnapshotState(currentText, string.Empty, SnapshotState.RangeLost,
                            "tracked-range-not-found");
                        if (changed)
                            ReportStatus("tracked_range_lost", "入力欄の文章構成が変わり、音声入力部分を追跡できなくなりました。");
                    }
                }
                else
                {
                    var changed = SetSnapshotState(null, null, SnapshotState.ReadFailed, readState);
                    if (changed)
                        Log("editor_read_failed", $"{readState};version={_snapshot.Version}");
                    UpdateTrackedEditorFocus(null);
                    if (IsRecoverableEditorFailure(readState))
                    {
                        ReportStatus("editor_rebind_unavailable",
                            "入力欄を安全に再確認できず、自動登録の監視を終了しました。次の音声入力から再開します。",
                            readState);
                        return;
                    }
                }

                while (_pendingSubmissions.TryDequeue(out var candidate))
                {
                    await ProcessSubmissionCandidateAsync(candidate, GetCurrentEditorAccess,
                        TryRebindEditor, monitorToken).ConfigureAwait(false);
                    if (Volatile.Read(ref _submissionCompleted) != 0) return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log("monitor_cancelled");
        }
        catch (OperationCanceledException)
        {
            ReportStatus("monitor_timeout", "監視時間を過ぎたため、自動登録を終了しました。");
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            ReportStatus("monitor_exception", "入力欄の追跡中にエラーが発生し、監視を終了しました。", ex.GetType().Name);
        }
        finally
        {
            _readySignal.TrySetResult(false);
            if (root is not null && invokedHandler is not null)
            {
                try { Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, root, invokedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (editorEventsAttached && editor is not null) DetachEditorEvents(editor);
            if (Volatile.Read(ref _disposed) == 0) Dispose();
        }
    }

    private void OnAutomationEvent(object sender, AutomationEventArgs args)
    {
        if (Volatile.Read(ref _initialized) == 0 || Volatile.Read(ref _disposed) != 0 ||
            sender is not AutomationElement element)
            return;

        try
        {
            var current = element.Current;
            if (current.ControlType != ControlType.Button || !current.IsEnabled) return;
            var name = current.Name ?? string.Empty;
            if (name.Contains("送信", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("send", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("投稿", StringComparison.OrdinalIgnoreCase))
                SignalSubmit("send-button");
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0 &&
            wParam.ToInt32() is WmKeyUp or WmSysKeyUp)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (key.VirtualKey == VkReturn && (key.Flags & LlkhfInjected) == 0)
                Interlocked.Exchange(ref _enterKeyDown, 0);
        }
        else if (code >= 0 && Volatile.Read(ref _initialized) != 0 && Volatile.Read(ref _disposed) == 0 &&
                 wParam.ToInt32() is WmKeyDown or WmSysKeyDown)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (key.VirtualKey == VkReturn && (key.Flags & LlkhfInjected) == 0 &&
                Interlocked.Exchange(ref _enterKeyDown, 1) == 0 &&
                (GetAsyncKeyState(VkShift) & 0x8000) == 0 &&
                GetForegroundWindow() == _targetWindow)
                SignalSubmit("enter");
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void SignalSubmit(string source)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SubmitCandidate? candidate = null;
        var deduplicated = false;
        var alreadyCompleted = false;
        string? ignoredReason = null;
        lock (_snapshotLock)
        {
            if (_submissionCompleted != 0)
                alreadyCompleted = true;
            else if (source == "enter" && !WasTrackedEditorFocusedRecently())
                ignoredReason = "tracked-editor-focus-not-current";
            else
            {
                var now = Stopwatch.GetTimestamp();
                var sameSignalWindow = now - _lastSignalTimestamp < Stopwatch.Frequency / 4;
                var duplicateCrossSource = sameSignalWindow &&
                                           _snapshot.Version == _lastSignalSnapshotVersion &&
                                           !string.Equals(source, _lastSignalSource, StringComparison.Ordinal);
                var duplicateButtonInvoke = source == "send-button" && _lastSignalSource == "send-button" &&
                                           _snapshot.Version == _lastSignalSnapshotVersion && sameSignalWindow;
                if (duplicateCrossSource || duplicateButtonInvoke)
                {
                    deduplicated = true;
                }
                else
                {
                    var captured = CreateCandidateLocked(source, DateTimeOffset.UtcNow);
                    if (!captured.IsValid)
                    {
                        ignoredReason = $"snapshot-not-fresh:{captured.Snapshot.State}";
                    }
                    else
                    {
                        candidate = captured;
                        _latestCandidateId = candidate.Id;
                        _lastSignalSnapshotVersion = _snapshot.Version;
                        _lastSignalTimestamp = now;
                        _lastSignalSource = source;
                    }
                }
            }
        }

        if (alreadyCompleted) return;
        if (ignoredReason is not null)
        {
            Log("submit_signal_ignored", $"source={source};reason={ignoredReason}");
            return;
        }
        if (deduplicated)
        {
            Interlocked.Increment(ref _deduplicatedSignalCount);
            return;
        }
        if (candidate is null) return;

        EnqueueSubmission(candidate);
    }

    private void EnqueueSubmission(SubmitCandidate candidate)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _pendingSubmissions.Enqueue(candidate);
        try
        {
            if (_submitSignal.CurrentCount == 0) _submitSignal.Release();
        }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private async Task ProcessSubmissionCandidateAsync(
        SubmitCandidate candidate,
        Func<(AutomationElement Element, TextPattern? TextPattern, ValuePattern? ValuePattern)> getEditorAccess,
        Func<bool> tryRebindEditor,
        CancellationToken cancellationToken)
    {
        var snapshot = candidate.Snapshot;
        Log("submit_candidate_captured",
            $"source={candidate.Source};valid={candidate.IsValid};recent-preclear={candidate.UsedRecentPreClearSnapshot};signal-utc={candidate.SignalTime:O};{SnapshotDescription(snapshot)}",
            0, candidate.Id);
        if (!candidate.IsValid || snapshot.State != SnapshotState.Valid ||
            string.IsNullOrWhiteSpace(snapshot.Segment))
        {
            Log("submit_snapshot_invalid",
                $"source={candidate.Source};{SnapshotDescription(snapshot)}", 0, candidate.Id);
            return;
        }

        var correction = !string.Equals(snapshot.Segment, _originalSegment, StringComparison.Ordinal);
        var confirmationReadCount = 0;
        var confirmationReadFailureCount = 0;
        var observedChangedText = false;
        var observedClearedText = false;
        var clearEvidence = "none";

        for (var confirmAttempt = 0; confirmAttempt < 8; confirmAttempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken).ConfigureAwait(false);
            if (candidate.Id != Volatile.Read(ref _latestCandidateId))
            {
                Log("submit_candidate_superseded", $"source={candidate.Source};newer-candidate-exists",
                    confirmAttempt, candidate.Id);
                return;
            }
            var editorAccess = getEditorAccess();
            if (!IsSameTrackedEditor(editorAccess.Element))
            {
                confirmationReadFailureCount++;
                Log("submit_confirmation_editor_mismatch", "target-or-runtime-id-changed",
                    confirmationReadFailureCount, candidate.Id);
                continue;
            }
            if (!TryReadText(editorAccess.Element, editorAccess.TextPattern, editorAccess.ValuePattern,
                    out var afterSubmit, out var readState))
            {
                if (IsRecoverableEditorFailure(readState) && tryRebindEditor())
                {
                    editorAccess = getEditorAccess();
                    if (!IsSameTrackedEditor(editorAccess.Element))
                    {
                        confirmationReadFailureCount++;
                        clearEvidence = "rebound-editor-identity-mismatch";
                        continue;
                    }
                    if (!TryReadText(editorAccess.Element, editorAccess.TextPattern, editorAccess.ValuePattern,
                            out afterSubmit, out var reboundReadState))
                    {
                        confirmationReadFailureCount++;
                        clearEvidence = $"rebind-read-failed:{reboundReadState}";
                        continue;
                    }
                    readState = $"rebound-same-runtime-id;{reboundReadState}";
                }
                else
                {
                    confirmationReadFailureCount++;
                    clearEvidence = readState;
                    continue;
                }
            }

            confirmationReadCount++;
            afterSubmit = NormalizeLineEndings(afterSubmit);
            if (afterSubmit.Length > MaxDocumentLength)
            {
                ReportStatus("post_submit_text_too_long", "送信後の入力欄が長いため、登録を見送りました。",
                    $"candidate={candidate.Id};read={readState}", confirmationReadCount);
                return;
            }

            var editorChanged = !string.Equals(afterSubmit, snapshot.FullText, StringComparison.Ordinal);
            var contentCleared = IsTrackedRangeCleared(afterSubmit, out var evidence);
            observedChangedText |= editorChanged;
            observedClearedText |= contentCleared;
            if (!editorChanged || !contentCleared) continue;

            clearEvidence = evidence;
            var accepted = false;
            lock (_snapshotLock)
            {
                if (_submissionCompleted == 0 && _latestCandidateId == candidate.Id)
                {
                    _submissionCompleted = 1;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                Log("submit_candidate_superseded", "newer-candidate-arrived-before-confirmation",
                    confirmationReadCount, candidate.Id);
                return;
            }

            Log("submission_confirmed",
                $"source={candidate.Source};evidence={clearEvidence};snapshot={SnapshotDescription(snapshot)};read={readState}",
                confirmationReadCount, candidate.Id);
            if (!correction)
            {
                ReportStatus("submitted_without_correction", "手修正が見つからなかったため、辞書は変更していません。",
                    $"candidate={candidate.Id};evidence={clearEvidence}", confirmationReadCount, candidate.Id);
                return;
            }

            ReportStatus("submission_ready_for_learning", "送信を確認しました。修正候補を判定しています。",
                $"candidate={candidate.Id};evidence={clearEvidence}", confirmationReadCount, candidate.Id);
            SubmissionConfirmed?.Invoke(this,
                new DictionaryCorrectionSubmission(_originalSegment, snapshot.Segment, _monitorId, candidate.Id));
            return;
        }

        var reason = confirmationReadCount == 0
            ? "editor-unreadable-or-identity-changed"
            : !observedChangedText
                ? "field-unchanged"
                : !observedClearedText
                    ? "tracked-text-retained-or-range-lost"
                    : "inconsistent-editor-state";
        var finalEditorAccess = getEditorAccess();
        var providerState = DescribeTextProviders(finalEditorAccess.Element, finalEditorAccess.TextPattern,
            finalEditorAccess.ValuePattern, snapshot.FullText);
        ReportStatus("submission_not_confirmed", "送信後の入力欄の変化を確認できず、登録を見送りました。",
            $"candidate={candidate.Id};source={candidate.Source};reason={reason};{SnapshotDescription(snapshot)};{providerState};last-read={clearEvidence}",
            confirmationReadCount + confirmationReadFailureCount, candidate.Id);
    }

    private void ReportStatus(string stage, string message, string detail = "none", int count = 0,
        long? candidateId = null)
    {
        Log(stage, detail, count, candidateId);
        StatusChanged?.Invoke(message);
    }

    private bool TryExtractSegment(string fullText, out string segment)
    {
        segment = string.Empty;
        if (!fullText.StartsWith(_prefix, StringComparison.Ordinal) ||
            (_suffix.Length > 0 && !fullText.EndsWith(_suffix, StringComparison.Ordinal)))
            return false;

        var end = fullText.Length - _suffix.Length;
        if (end < _prefix.Length) return false;
        segment = fullText[_prefix.Length..end];
        return true;
    }

    private void RefreshSnapshot(
        AutomationElement editor,
        TextPattern? textPattern,
        ValuePattern? valuePattern)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!TryReadText(editor, textPattern, valuePattern, out var currentText, out var readState))
        {
            if (SetSnapshotState(null, null, SnapshotState.ReadFailed, readState))
                Log("editor_read_failed", $"{readState};version={_snapshot.Version}");
            return;
        }
        currentText = NormalizeLineEndings(currentText);
        if (currentText.Length > MaxDocumentLength)
        {
            SetSnapshotState(currentText, string.Empty, SnapshotState.TooLong, $"length={currentText.Length}");
            return;
        }
        if (!TryExtractSegment(currentText, out var segment))
        {
            if (SetSnapshotState(currentText, string.Empty, SnapshotState.RangeLost, "tracked-range-not-found"))
                ReportStatus("tracked_range_lost", "入力欄の文章構成が変わり、音声入力部分を追跡できなくなりました。");
            return;
        }
        SetSnapshot(currentText, segment);
    }

    private void SetSnapshot(string fullText, string segment)
    {
        lock (_snapshotLock)
            SetSnapshotLocked(fullText, segment,
                string.IsNullOrWhiteSpace(segment) ? SnapshotState.EmptySegment : SnapshotState.Valid,
                string.IsNullOrWhiteSpace(segment) ? "tracked-segment-empty" : "readable");
    }

    private bool SetSnapshotState(string? fullText, string? segment, SnapshotState state, string readState)
    {
        lock (_snapshotLock)
        {
            var prior = _snapshot;
            var nextFullText = fullText ?? prior.FullText;
            var nextSegment = segment ?? prior.Segment;
            var changed = prior.State != state || prior.ReadState != readState ||
                          !string.Equals(prior.FullText, nextFullText, StringComparison.Ordinal) ||
                          !string.Equals(prior.Segment, nextSegment, StringComparison.Ordinal);
            SetSnapshotLocked(nextFullText, nextSegment, state, readState);
            return changed;
        }
    }

    private void SetSnapshotLocked(string fullText, string segment, SnapshotState state, string readState)
    {
        var contentChanged = _snapshot.State != state || _snapshot.ReadState != readState ||
                             !string.Equals(_snapshot.FullText, fullText, StringComparison.Ordinal) ||
                             !string.Equals(_snapshot.Segment, segment, StringComparison.Ordinal);
        var version = contentChanged ? ++_snapshotVersion : _snapshot.Version;
        _snapshot = new EditorSnapshot(version, Stopwatch.GetTimestamp(), fullText, segment, state, readState);
        if (state == SnapshotState.Valid) _lastValidSnapshot = _snapshot;
    }

    private SubmitCandidate CreateCandidateLocked(string source, DateTimeOffset signalTime)
    {
        var snapshot = _snapshot;
        var usedRecentPreClearSnapshot = false;
        if (source == "send-button" && snapshot.State == SnapshotState.EmptySegment &&
            _lastValidSnapshot.State == SnapshotState.Valid &&
            GetSnapshotAgeMilliseconds(_lastValidSnapshot) <= RecentPreClearSnapshotMs)
        {
            snapshot = _lastValidSnapshot;
            usedRecentPreClearSnapshot = true;
        }
        var age = GetSnapshotAgeMilliseconds(snapshot);
        var valid = snapshot.State == SnapshotState.Valid &&
                    !string.IsNullOrWhiteSpace(snapshot.Segment) && age <= SnapshotFreshnessMs;
        return new SubmitCandidate(++_nextCandidateId, source, signalTime, snapshot, valid,
            usedRecentPreClearSnapshot);
    }

    private void Log(string stage, string detail = "none", int count = 0, long? candidateId = null) =>
        DictionaryLearningDiagnosticLog.Write(stage, detail, count, _monitorId, candidateId);

    private static string SnapshotDescription(EditorSnapshot snapshot) =>
        $"version={snapshot.Version};state={snapshot.State};age-ms={GetSnapshotAgeMilliseconds(snapshot)};full-length={snapshot.FullText.Length};segment-length={snapshot.Segment.Length};read={snapshot.ReadState}";

    private static long GetSnapshotAgeMilliseconds(EditorSnapshot snapshot)
    {
        if (snapshot.ObservedTimestamp == 0) return long.MaxValue;
        return Math.Max(0, (long)Stopwatch.GetElapsedTime(snapshot.ObservedTimestamp).TotalMilliseconds);
    }

    private bool IsTrackedRangeCleared(string fullText, out string evidence)
    {
        if (string.IsNullOrWhiteSpace(fullText))
        {
            evidence = "same-editor-whole-field-empty";
            return true;
        }
        if (!TryExtractSegment(fullText, out var remainingSegment))
        {
            evidence = "range-mismatch-not-clear-evidence";
            return false;
        }
        if (string.IsNullOrWhiteSpace(remainingSegment))
        {
            evidence = "same-editor-tracked-range-empty";
            return true;
        }
        evidence = "tracked-range-retains-text";
        return false;
    }

    private bool IsSameTrackedEditor(AutomationElement editor)
    {
        if (!IsWithinWindow(editor, _targetWindow)) return false;
        if (_editorRuntimeId is null) return true;
        var currentId = GetRuntimeId(editor);
        return currentId is not null && string.Equals(currentId, _editorRuntimeId, StringComparison.Ordinal);
    }

    private void UpdateTrackedEditorFocus(AutomationElement? editor)
    {
        long focusedAt = 0;
        if (editor is not null && !string.IsNullOrWhiteSpace(_editorRuntimeId))
        {
            try
            {
                var focusedEditor = FindFocusedEditor(_targetWindow, out _, out _);
                if (focusedEditor is not null &&
                    string.Equals(GetRuntimeId(focusedEditor), _editorRuntimeId, StringComparison.Ordinal))
                    focusedAt = Stopwatch.GetTimestamp();
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        }
        Interlocked.Exchange(ref _trackedEditorFocusTimestamp, focusedAt);
    }

    private bool WasTrackedEditorFocusedRecently()
    {
        var focusedAt = Interlocked.Read(ref _trackedEditorFocusTimestamp);
        return focusedAt != 0 &&
               Stopwatch.GetElapsedTime(focusedAt) <= TimeSpan.FromMilliseconds(TrackedEditorFocusFreshnessMs);
    }

    private static string? GetRuntimeId(AutomationElement editor)
    {
        try
        {
            var runtimeId = editor.GetRuntimeId();
            return runtimeId.Length == 0 ? null : string.Join(",", runtimeId);
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return null;
        }
    }

    private static AutomationElement? FindFocusedEditor(
        IntPtr targetWindow,
        out TextPattern? textPattern,
        out ValuePattern? valuePattern)
    {
        textPattern = null;
        valuePattern = null;
        try
        {
            var element = AutomationElement.FocusedElement;
            var walker = TreeWalker.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 64 && IsWithinWindow(element, targetWindow); depth++)
            {
                if (!element.Current.IsPassword && element.Current.IsEnabled)
                {
                    var hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out var textValue) &&
                                  textValue is TextPattern;
                    var hasEditableValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                                           value is ValuePattern pattern && !pattern.Current.IsReadOnly;
                    if (hasText && (hasEditableValue || element.Current.ControlType == ControlType.Edit))
                    {
                        textPattern = (TextPattern)textValue!;
                        valuePattern = hasEditableValue ? (ValuePattern)value! : null;
                        return element;
                    }
                }
                element = walker.GetParent(element);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex)) { }
        return null;
    }

    private static bool TryReadText(
        AutomationElement element,
        TextPattern? textPattern,
        ValuePattern? valuePattern,
        out string text,
        out string readState)
    {
        text = string.Empty;
        readState = "unreadable";
        bool isPassword;
        try
        {
            isPassword = element.Current.IsPassword;
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            readState = $"element-metadata-failed:{ex.GetType().Name}";
            return false;
        }
        if (isPassword)
        {
            readState = "password-field-blocked";
            return false;
        }

        string? valueFailure = null;
        if (valuePattern is not null)
        {
            try
            {
                text = valuePattern.Current.Value ?? string.Empty;
                readState = "value-pattern";
                return true;
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
            {
                valueFailure = ex.GetType().Name;
            }
        }

        if (textPattern is not null)
        {
            try
            {
                text = textPattern.DocumentRange.GetText(MaxDocumentLength + 1);
                readState = valueFailure is null ? "text-pattern" : $"text-pattern-fallback-after:{valueFailure}";
                return true;
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
            {
                readState = $"text-pattern-failed:{ex.GetType().Name};value-pattern={valueFailure ?? "unavailable"}";
                return false;
            }
        }

        readState = valueFailure is not null
            ? $"value-pattern-failed:{valueFailure};text-pattern=unavailable"
            : "no-readable-text-pattern";
        return false;
    }

    private static string DescribeTextProviders(
        AutomationElement element,
        TextPattern? textPattern,
        ValuePattern? valuePattern,
        string beforeSubmit)
    {
        string metadataState;
        try
        {
            if (element.Current.IsPassword)
                return $"baseline-length={beforeSubmit.Length};element=password-blocked";
            metadataState = "available";
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return $"baseline-length={beforeSubmit.Length};element=unreadable:{ex.GetType().Name}";
        }
        var valueState = valuePattern is null
            ? "unavailable"
            : DescribeTextProvider(() => valuePattern.Current.Value ?? string.Empty, beforeSubmit);
        var textState = textPattern is null
            ? "unavailable"
            : DescribeTextProvider(
                () => textPattern.DocumentRange.GetText(MaxDocumentLength + 1), beforeSubmit);

        return $"baseline-length={beforeSubmit.Length};element={metadataState};value-pattern={valueState};text-pattern={textState}";
    }

    private static string DescribeTextProvider(Func<string> read, string beforeSubmit)
    {
        try
        {
            var current = NormalizeLineEndings(read());
            if (string.IsNullOrWhiteSpace(current)) return $"empty:length={current.Length}";
            return string.Equals(current, beforeSubmit, StringComparison.Ordinal)
                ? $"unchanged:length={current.Length}"
                : $"changed:length={current.Length}";
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return $"unreadable:{ex.GetType().Name}";
        }
    }

    private static bool IsWithinWindow(AutomationElement element, IntPtr targetWindow)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < 256; depth++)
        {
            try
            {
                if (element.Current.NativeWindowHandle != 0 &&
                    new IntPtr(element.Current.NativeWindowHandle) == targetWindow)
                    return true;
                element = walker.GetParent(element);
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { return false; }
        }
        return false;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or
            UnauthorizedAccessException or ArgumentException;

    private static bool IsRecoverableEditorFailure(string readState) =>
        readState.Contains(nameof(ElementNotAvailableException), StringComparison.Ordinal) ||
        readState.Contains(nameof(ObjectDisposedException), StringComparison.Ordinal);

    private enum SnapshotState
    {
        Unreadable,
        Valid,
        EmptySegment,
        RangeLost,
        ReadFailed,
        TooLong
    }

    private sealed record EditorSnapshot(
        long Version,
        long ObservedTimestamp,
        string FullText,
        string Segment,
        SnapshotState State,
        string ReadState)
    {
        public static EditorSnapshot Unreadable { get; } =
            new(0, 0, string.Empty, string.Empty, SnapshotState.Unreadable, "not-initialized");
    }

    private sealed record SubmitCandidate(
        long Id,
        string Source,
        DateTimeOffset SignalTime,
        EditorSnapshot Snapshot,
        bool IsValid,
        bool UsedRecentPreClearSnapshot);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
