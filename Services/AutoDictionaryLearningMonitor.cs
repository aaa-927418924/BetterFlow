using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed record DictionaryCorrectionSubmission(
    string OriginalText,
    string CorrectedText,
    string MonitorId,
    long CandidateId);

internal sealed record DictionaryLearningStatusUpdate(string MonitorId, string Message);

internal sealed class AutoDictionaryLearningMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLeftButtonDown = 0x0201;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfLowerIntegrityInjected = 0x02;
    private const uint LlMouseInjected = 0x01;
    private const uint LlMouseLowerIntegrityInjected = 0x02;
    private const int VkReturn = 0x0D;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int MaxDocumentLength = 24_000;
    private const int MaxMonitorMinutes = 10;
    private const int PastePropagationTimeoutMs = 2_000;
    private const int PastePropagationPollMs = 50;
    private const int SnapshotFreshnessMs = 1_500;
    private const int RecentPreClearSnapshotMs = 500;
    private const int TrackedEditorFocusFreshnessMs = 1_000;
    private const int SubmissionRecoveryWindowMs = 8_000;
    private const int UnsignaledSnapshotMaxAgeMs = 8_000;
    private const int MaxPlaceholderLength = 128;
    private const int MaxAutomationSearchNodes = 2_000;
    private const int MaxAutomationTreeDepth = 32;
    private const int MaxSendButtonHitTestNodes = 800;
    private const int MaxOutgoingProofNodes = 900;
    private const int MaxOutgoingProofAttemptsPerCandidate = 5;

    private static readonly HashSet<string> KnownComposerPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ask anything",
        "Ask anything...",
        "Ask anything…",
        "Message ChatGPT",
        "Message ChatGPT...",
        "Message ChatGPT…",
        "何でも聞いてください",
        "何でも質問してください",
        "何でも質問できます",
        "何か聞いてください",
        "何か聞いてみましょう",
        "メッセージを入力",
        "メッセージを入力してください",
        "ChatGPT にメッセージを送信する",
        "ChatGPT にメッセージを送信",
        "ChatGPTにメッセージを送信する",
        "ChatGPTにメッセージを送信"
    };

    private readonly IntPtr _targetWindow;
    private readonly int _targetProcessId;
    private readonly string _targetProcessName;
    private readonly string _pastedText;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _monitorWakeSignal = new(0, 1);
    private readonly object _snapshotLock = new();
    private readonly ConcurrentQueue<SubmitCandidate> _pendingSubmissions = new();
    private readonly ConcurrentQueue<AutomationNotice> _automationNotices = new();
    private readonly string _monitorId = Guid.NewGuid().ToString("N")[..8];
    private readonly TaskCompletionSource<bool> _readySignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Task? _monitorTask;
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private string _originalSegment = string.Empty;
    private EditorBinding? _editorBinding;
    private EditorFingerprint? _trackedEditorIdentity;
    private Dictionary<string, string> _baselineTextElementDigests = new(StringComparer.Ordinal);
    private EditorSnapshot _snapshot = EditorSnapshot.Unreadable;
    private EditorSnapshot _lastValidSnapshot = EditorSnapshot.Unreadable;
    private SubmitCandidate? _preparedApplicationSubmit;
    private SubmitCandidate? _outgoingProofCandidate;
    private long _snapshotVersion;
    private long _nextCandidateId;
    private long _latestCandidateId;
    private int _enterKeyDown;
    private long _lastSignalSnapshotVersion = -1;
    private long _lastSignalTimestamp;
    private string _lastSignalSource = string.Empty;
    private int _submissionCompleted;
    private long _pendingSubmissionId;
    private long _pendingSubmissionTimestamp;
    private int _focusNoticePending;
    private int _deduplicatedSignalCount;
    private int _initialized;
    private int _disposed;
    private long _trackedEditorFocusTimestamp;
    private long _lastMouseMissDiagnosticTimestamp;
    private long _lastOutgoingProofCheckTimestamp;
    private long _nextEditorGeneration;
    private int _trackingState = (int)EditorTrackingState.Starting;

    private AutoDictionaryLearningMonitor(IntPtr targetWindow, string pastedText)
    {
        _targetWindow = targetWindow;
        GetWindowThreadProcessId(targetWindow, out var processId);
        _targetProcessId = unchecked((int)processId);
        _targetProcessName = GetProcessName(_targetProcessId);
        _pastedText = NormalizeLineEndings(pastedText);
        _token = _cancellation.Token;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public event EventHandler<DictionaryCorrectionSubmission>? SubmissionConfirmed;
    public event EventHandler<DictionaryLearningStatusUpdate>? StatusChanged;

    public string MonitorId => _monitorId;

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
            if (_preparedApplicationSubmit is { } previous)
                ClearPendingSubmission(previous.Id);
            _preparedApplicationSubmit = null;
            candidate = CreateCandidateLocked("application-send", DateTimeOffset.UtcNow);
            valid = candidate.IsValid;
            if (valid)
            {
                _preparedApplicationSubmit = candidate;
                Interlocked.Exchange(ref _pendingSubmissionId, candidate.Id);
                Interlocked.Exchange(ref _pendingSubmissionTimestamp, Stopwatch.GetTimestamp());
            }
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
        {
            Interlocked.CompareExchange(ref _pendingSubmissionId, 0, candidate.Id);
            Log("application_submit_cancelled",
                $"send-input-failed;{SnapshotDescription(candidate.Snapshot)}", 0, candidate.Id);
        }
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

        monitor._mouseHook = SetWindowsHookEx(WhMouseLl, monitor._mouseProc, module, 0);
        monitor.Log("monitor_started",
            $"target-process={monitor._targetProcessName};mouse-hook={(monitor._mouseHook == IntPtr.Zero ? "unavailable" : "ready")}");
        if (monitor._mouseHook == IntPtr.Zero)
            monitor.Log("mouse_hook_unavailable", $"error={Marshal.GetLastWin32Error()}");
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
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        lock (_snapshotLock)
        {
            _prefix = string.Empty;
            _suffix = string.Empty;
            _originalSegment = string.Empty;
            _snapshot = EditorSnapshot.Unreadable;
            _lastValidSnapshot = EditorSnapshot.Unreadable;
            _preparedApplicationSubmit = null;
            _outgoingProofCandidate = null;
            Volatile.Write(ref _editorBinding, null);
            _trackedEditorIdentity = null;
            Interlocked.Exchange(ref _pendingSubmissionId, 0);
            Interlocked.Exchange(ref _pendingSubmissionTimestamp, 0);
            Interlocked.Exchange(ref _focusNoticePending, 0);
            Volatile.Write(ref _baselineTextElementDigests, new Dictionary<string, string>(StringComparer.Ordinal));
        }
        while (_pendingSubmissions.TryDequeue(out _)) { }
        while (_automationNotices.TryDequeue(out _)) { }
        _monitorWakeSignal.Dispose();
        _cancellation.Dispose();
    }

    private async Task MonitorAsync()
    {
        AutomationElement? root = null;
        AutomationEventHandler? invokedHandler = null;
        AutomationFocusChangedEventHandler? focusChangedHandler = null;
        EditorBinding? eventBinding = null;
        AutomationEventHandler? textChangedHandler = null;
        AutomationPropertyChangedEventHandler? valueChangedHandler = null;
        var token = _token;
        var monitorToken = token;
        var recoveryStartedAt = 0L;
        var lastRebindDiagnosticKey = string.Empty;
        var lastRebindDiagnosticAt = 0L;

        void LogRebindFailure(string stage, string detail)
        {
            var reasonKey = detail.Split(';', 2)[0];
            var key = stage + ":" + reasonKey;
            var now = Stopwatch.GetTimestamp();
            if (string.Equals(lastRebindDiagnosticKey, key, StringComparison.Ordinal) &&
                lastRebindDiagnosticAt != 0 &&
                Stopwatch.GetElapsedTime(lastRebindDiagnosticAt, now) < TimeSpan.FromSeconds(2))
                return;
            lastRebindDiagnosticKey = key;
            lastRebindDiagnosticAt = now;
            Log(stage, detail);
        }

        void DetachEditorEvents()
        {
            if (eventBinding is null) return;
            if (textChangedHandler is not null)
            {
                try
                {
                    Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent,
                        eventBinding.Element, textChangedHandler);
                }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (valueChangedHandler is not null)
            {
                try
                {
                    Automation.RemoveAutomationPropertyChangedEventHandler(eventBinding.Element,
                        valueChangedHandler);
                }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            eventBinding = null;
            textChangedHandler = null;
            valueChangedHandler = null;
        }

        void AttachEditorEvents(EditorBinding binding)
        {
            if (eventBinding?.Generation == binding.Generation) return;
            DetachEditorEvents();
            var generation = binding.Generation;
            textChangedHandler = (_, _) =>
                QueueAutomationNotice(new AutomationNotice(AutomationNoticeKind.EditorChanged, generation));
            valueChangedHandler = (_, _) =>
                QueueAutomationNotice(new AutomationNotice(AutomationNoticeKind.EditorChanged, generation));
            try
            {
                Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, binding.Element,
                    TreeScope.Element, textChangedHandler);
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            if (binding.ValuePattern is not null)
            {
                try
                {
                    Automation.AddAutomationPropertyChangedEventHandler(binding.Element, TreeScope.Element,
                        valueChangedHandler, ValuePattern.ValueProperty);
                }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            eventBinding = binding;
        }

        bool TryRebindEditor(out string reason)
        {
            var identity = _trackedEditorIdentity;
            if (identity is null)
            {
                reason = "tracked-editor-identity-unavailable";
                return false;
            }

            var priorBinding = Volatile.Read(ref _editorBinding);
            var lastValidSnapshot = _lastValidSnapshot;
            var candidates = FindMatchingEditorCandidates(_targetWindow, identity,
                priorBinding?.DocumentRoot, lastValidSnapshot.FullText,
                HasPendingSubmissionIntent(),
                out var examined, out var rejectionCounts, out var focusedEditorFound);
            if (candidates.Count == 0)
            {
                reason = "no-matching-editor";
                LogRebindFailure("editor_rebind_failed",
                    $"reason={reason};focus={(focusedEditorFound ? "editable-found" : "none")};examined={examined};rejected={FormatRejectionCounts(rejectionCounts)}");
                return false;
            }

            var ordered = candidates.OrderByDescending(candidate => candidate.Score).ToArray();
            if (ordered.Length > 1 && ordered[0].Score - ordered[1].Score < 20)
            {
                reason = "ambiguous-matching-editors";
                LogRebindFailure("editor_rebind_rejected",
                    $"reason={reason};focus={(focusedEditorFound ? "editable-found" : "none")};candidate-count={ordered.Length};score-gap={ordered[0].Score - ordered[1].Score};examined={examined}");
                return false;
            }

            var match = ordered[0];
            var previous = Volatile.Read(ref _editorBinding);
            var sameRuntimeId = previous?.RuntimeId is not null &&
                                string.Equals(previous.RuntimeId, match.Fingerprint.RuntimeId, StringComparison.Ordinal);
            var sameElement = previous is not null && AreSameAutomationElement(previous.Element, match.Element);
            var generation = sameElement
                ? previous!.Generation
                : Interlocked.Increment(ref _nextEditorGeneration);
            var replacement = CreateEditorBinding(match.Element, match.TextPattern, match.ValuePattern,
                generation, match.Fingerprint);
            if (replacement is null)
            {
                reason = "replacement-fingerprint-unavailable";
                LogRebindFailure("editor_rebind_rejected", $"reason={reason};examined={examined}");
                return false;
            }

            Volatile.Write(ref _editorBinding, replacement);
            if (!sameElement) AttachEditorEvents(replacement);
            reason = sameRuntimeId
                ? sameElement ? "same-element-pattern-refresh" : "same-runtime-id-new-element"
                : "same-document-structure";
            if (!sameElement)
                Log("editor_rebound",
                    $"reason={reason};focus={(match.IsFocused ? "tracked-candidate" : "not-focused")};bounds={DescribeBoundsEvidence(identity.Bounds, match.Fingerprint.Bounds)};generation={replacement.Generation};score={match.Score};candidate-count={ordered.Length};examined={examined}");
            return true;
        }

        bool TryReadCurrentEditor(out string text, out string readState)
        {
            text = string.Empty;
            var binding = Volatile.Read(ref _editorBinding);
            readState = binding is null ? "editor-not-bound" : "unreadable";
            if (binding is null) return false;
            if (TryReadText(binding.Element, binding.TextPattern, binding.ValuePattern, out text, out readState))
                return true;

            var firstReadState = readState;
            if (!TryRebindEditor(out var rebindReason))
            {
                readState = $"{firstReadState};rebind={rebindReason}";
                return false;
            }

            binding = Volatile.Read(ref _editorBinding);
            var reboundReadState = binding is null ? "editor-not-bound" : "unreadable";
            if (binding is not null &&
                TryReadText(binding.Element, binding.TextPattern, binding.ValuePattern, out text, out reboundReadState))
            {
                readState = $"rebound:{rebindReason};{reboundReadState}";
                return true;
            }
            readState = $"rebind-read-failed:{rebindReason};{reboundReadState}";
            return false;
        }

        EditorAccess? GetCurrentEditorAccess()
        {
            var binding = Volatile.Read(ref _editorBinding);
            return binding is null ? null : new EditorAccess(binding.Element, binding.TextPattern,
                binding.ValuePattern, binding.Generation, binding.RuntimeId, binding.Fingerprint.DocumentRuntimeId);
        }

        void DrainAutomationNotices()
        {
            while (_automationNotices.TryDequeue(out var notice))
            {
                if (notice.Kind == AutomationNoticeKind.Invoked)
                {
                    if (notice.Element is not null && IsSendButton(notice.Element))
                        SignalSubmit("send-button");
                    continue;
                }

                if (notice.Kind == AutomationNoticeKind.MouseButtonDown)
                {
                    if (notice.TargetForegroundMatched)
                    {
                        if (TryFindSendButtonAtPoint(notice.X, notice.Y, out _, out var hitTestResult))
                        {
                            var structuralMatch = hitTestResult == "composer-structural-button";
                            Log("send_button_click_detected",
                                structuralMatch
                                    ? "source=mouse;control=unnamed-button-in-tracked-composer"
                                    : "source=mouse;control=enabled-send-button");
                            SignalSubmit(structuralMatch ? "mouse-composer-button" : "mouse-send-button",
                                notice.PreparedCandidate);
                        }
                        else if (hitTestResult == "button-name-not-recognized")
                        {
                            LogMouseHitTestMiss(hitTestResult);
                        }
                        else LogMouseHitTestMiss(hitTestResult);
                    }
                    continue;
                }

                if (notice.Kind == AutomationNoticeKind.EditorChanged)
                {
                    var currentGeneration = Volatile.Read(ref _editorBinding)?.Generation ?? 0;
                    if (notice.Generation != currentGeneration)
                        Log("editor_event_ignored", $"reason=stale-generation;event-generation={notice.Generation};current-generation={currentGeneration}");
                }
                else if (notice.Kind == AutomationNoticeKind.FocusChanged)
                {
                    Interlocked.Exchange(ref _focusNoticePending, 0);
                    if (notice.Element is not null && IsWithinWindow(notice.Element, _targetWindow))
                    {
                        var binding = Volatile.Read(ref _editorBinding);
                        if (binding is not null && IsWithinElement(notice.Element, binding.Element))
                            Interlocked.Exchange(ref _trackedEditorFocusTimestamp, Stopwatch.GetTimestamp());
                    }
                }
            }
        }

        void SetTrackingState(EditorTrackingState state, string reason)
        {
            var previous = (EditorTrackingState)Interlocked.Exchange(ref _trackingState, (int)state);
            if (previous == state) return;
            Log("editor_tracking_state", $"from={previous};to={state};reason={reason}");
            switch (state)
            {
                case EditorTrackingState.Reacquiring:
                    StatusChanged?.Invoke(this, new DictionaryLearningStatusUpdate(_monitorId,
                        "入力欄を再確認しています。自動登録の監視は続いています。"));
                    break;
                case EditorTrackingState.Suspended:
                    StatusChanged?.Invoke(this, new DictionaryLearningStatusUpdate(_monitorId,
                        "入力欄を一時的に確認できません。復旧を待っています。"));
                    break;
                case EditorTrackingState.Tracking when previous is EditorTrackingState.Reacquiring or EditorTrackingState.Suspended:
                    StatusChanged?.Invoke(this, new DictionaryLearningStatusUpdate(_monitorId,
                        "入力欄の追跡が復旧し、自動登録の監視を続けています。"));
                    break;
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(MaxMonitorMinutes));
            monitorToken = timeout.Token;

            for (var attempt = 0; attempt < 15 && Volatile.Read(ref _editorBinding) is null; attempt++)
            {
                monitorToken.ThrowIfCancellationRequested();
                var focused = FindFocusedEditor(_targetWindow, out var textPattern, out var valuePattern);
                if (focused is not null)
                {
                    var binding = CreateEditorBinding(focused, textPattern, valuePattern,
                        Interlocked.Increment(ref _nextEditorGeneration));
                    if (binding is not null)
                    {
                        Volatile.Write(ref _editorBinding, binding);
                        _trackedEditorIdentity = binding.Fingerprint;
                        AttachEditorEvents(binding);
                    }
                }
                if (Volatile.Read(ref _editorBinding) is null)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), monitorToken).ConfigureAwait(false);
            }

            var initialBinding = Volatile.Read(ref _editorBinding);
            if (initialBinding is null)
            {
                ReportStatus("editor_not_found", "入力欄を読み取れず、自動登録の監視を開始できませんでした。");
                return;
            }
            Log("editor_bound", initialBinding.RuntimeId is null ? "runtime-id-unavailable" : "runtime-id-captured");
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
            Volatile.Write(ref _baselineTextElementDigests,
                CaptureInitialReadOnlyTextElementDigests(initialBinding));
            Volatile.Write(ref _initialized, 1);
            UpdateTrackedEditorFocus(initialBinding.Element);
            ReportStatus("tracking_started", "音声入力した文章を送信するまで監視しています。");

            try
            {
                root = AutomationElement.FromHandle(_targetWindow);
                invokedHandler = (sender, _) =>
                {
                    if (sender is AutomationElement element)
                        QueueAutomationNotice(new AutomationNotice(AutomationNoticeKind.Invoked, 0, element));
                };
                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, root,
                    TreeScope.Descendants, invokedHandler);
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { }

            focusChangedHandler = (sender, _) =>
            {
                if (sender is not AutomationElement element || Volatile.Read(ref _disposed) != 0) return;
                if (Interlocked.Exchange(ref _focusNoticePending, 1) == 0)
                    QueueAutomationNotice(new AutomationNotice(AutomationNoticeKind.FocusChanged, 0, element));
            };
            try { Automation.AddAutomationFocusChangedEventHandler(focusChangedHandler); }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }

            _readySignal.TrySetResult(true);

            while (!monitorToken.IsCancellationRequested)
            {
                await _monitorWakeSignal.WaitAsync(TimeSpan.FromMilliseconds(500), monitorToken).ConfigureAwait(false);
                DrainAutomationNotices();
                if (Volatile.Read(ref _disposed) != 0) return;

                var deduplicatedCount = Interlocked.Exchange(ref _deduplicatedSignalCount, 0);
                if (deduplicatedCount > 0)
                    Log("submit_signal_deduplicated", "repeat-or-cross-source", deduplicatedCount);
                var composerCleared = false;
                var editorReadable = TryReadCurrentEditor(out var currentText, out var readState);
                if (editorReadable)
                {
                    recoveryStartedAt = 0;
                    SetTrackingState(EditorTrackingState.Tracking, "editor-readable");
                    var currentBinding = Volatile.Read(ref _editorBinding);
                    UpdateTrackedEditorFocus(currentBinding?.Element);
                    currentText = NormalizeLineEndings(currentText);
                    if (currentText.Length > MaxDocumentLength)
                    {
                        ReportStatus("editor_text_too_long", "入力欄が長くなったため、自動登録の監視を終了しました。");
                        return;
                    }
                    composerCleared = IsComposerCleared(currentText);
                    if (composerCleared)
                    {
                        SetSnapshot(currentText, string.Empty);
                    }
                    else if (TryExtractSegment(currentText, out var segment))
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
                    if (recoveryStartedAt == 0) recoveryStartedAt = Stopwatch.GetTimestamp();
                    var recoveryDuration = Stopwatch.GetElapsedTime(recoveryStartedAt);
                    var nextState = recoveryDuration >= TimeSpan.FromSeconds(2)
                        ? EditorTrackingState.Suspended
                        : EditorTrackingState.Reacquiring;
                    SetTrackingState(nextState, readState);
                }

                var currentSnapshot = GetSnapshot();
                var canCheckOutgoingProof = composerCleared || currentSnapshot.State == SnapshotState.RangeLost ||
                                            !editorReadable &&
                                            (EditorTrackingState)Volatile.Read(ref _trackingState) == EditorTrackingState.Suspended;
                if (canCheckOutgoingProof)
                {
                    var trigger = composerCleared ? "composer-cleared" :
                        currentSnapshot.State == SnapshotState.RangeLost ? "tracked-range-lost" : "editor-suspended";
                    if (TryCompleteUnsignaledSubmission(trigger)) return;
                }
                else if (editorReadable)
                {
                    ClearUnsignaledSubmissionCandidate();
                }

                while (_pendingSubmissions.TryDequeue(out var candidate))
                {
                    var retry = await ProcessSubmissionCandidateAsync(candidate, GetCurrentEditorAccess,
                        () => TryRebindEditor(out _), monitorToken).ConfigureAwait(false);
                    if (retry)
                    {
                        _pendingSubmissions.Enqueue(candidate);
                        break;
                    }
                    if (Volatile.Read(ref _submissionCompleted) != 0)
                    {
                        SetTrackingState(EditorTrackingState.Submitted, "submission-confirmed");
                        return;
                    }
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
            ReportStatus("monitor_exception", "入力欄の監視中に回復できないエラーが発生しました。", ex.GetType().Name);
        }
        finally
        {
            _readySignal.TrySetResult(false);
            if (root is not null && invokedHandler is not null)
            {
                try { Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, root, invokedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (focusChangedHandler is not null)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(focusChangedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            DetachEditorEvents();
            if (Volatile.Read(ref _disposed) == 0) Dispose();
        }
    }

    private static bool IsSendButton(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var isButton = current.ControlType == ControlType.Button ||
                           current.ControlType == ControlType.Custom &&
                           element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            if (!isButton || !current.IsEnabled) return false;
            var labels = $"{current.Name} {current.AutomationId} {current.HelpText}";
            return labels.Contains("送信", StringComparison.OrdinalIgnoreCase) ||
                   labels.Contains("send", StringComparison.OrdinalIgnoreCase) ||
                   labels.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                   labels.Contains("投稿", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return false;
    }

    private bool TryFindSendButtonAtPoint(int x, int y, out AutomationElement? sendButton, out string hitTestResult)
    {
        sendButton = null;
        hitTestResult = "no-button-at-point";
        var point = new System.Windows.Point(x, y);
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var walker = TreeWalker.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 32 && IsWithinWindow(element, _targetWindow); depth++)
            {
                if (IsSendButton(element))
                {
                    sendButton = element;
                    hitTestResult = "named-send-button";
                    return true;
                }
                if (IsLikelyComposerActionButton(element, point))
                {
                    sendButton = element;
                    hitTestResult = "composer-structural-button";
                    return true;
                }
                element = walker.GetParent(element);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }

        try
        {
            var windowRoot = AutomationElement.FromHandle(_targetWindow);
            foreach (var element in EnumerateWindowElements(windowRoot, MaxSendButtonHitTestNodes, 20))
            {
                if (!IsWithinWindow(element, _targetWindow)) continue;
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || !bounds.Contains(point)) continue;
                if (IsSendButton(element))
                {
                    sendButton = element;
                    hitTestResult = "window-scoped-send-button-bounds";
                    return true;
                }
                if (IsLikelyComposerActionButton(element, point))
                {
                    sendButton = element;
                    hitTestResult = "composer-structural-button";
                    return true;
                }
                var current = element.Current;
                var isButton = current.ControlType == ControlType.Button ||
                               current.ControlType == ControlType.Custom &&
                               element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                if (isButton && current.IsEnabled) hitTestResult = "button-name-not-recognized";
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        if (hitTestResult == "no-button-at-point") hitTestResult = "window-scoped-send-button-not-found";
        return false;
    }

    private bool IsLikelyComposerActionButton(AutomationElement element, System.Windows.Point point)
    {
        try
        {
            var current = element.Current;
            var isButton = current.ControlType == ControlType.Button ||
                           current.ControlType == ControlType.Custom &&
                           element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            if (!isButton || !current.IsEnabled || !IsWithinWindow(element, _targetWindow)) return false;

            var binding = Volatile.Read(ref _editorBinding);
            var identity = _trackedEditorIdentity;
            if (binding is null || identity is null || !IsSameTrackedEditor(binding.Element) ||
                !IsWithinDocumentScope(element, identity.DocumentRuntimeId, binding.DocumentRoot))
                return false;

            var buttonBounds = current.BoundingRectangle;
            if (buttonBounds.IsEmpty || !buttonBounds.Contains(point)) return false;
            var commonAncestor = FindNearestCommonAncestor(element, binding.Element,
                maxDepth: 8, out var buttonDepth, out var editorDepth);
            if (commonAncestor is null || buttonDepth > 4 || editorDepth > 4) return false;

            var container = commonAncestor.Current;
            if (container.ControlType != ControlType.Group &&
                container.ControlType != ControlType.Custom &&
                container.ControlType != ControlType.Pane)
                return false;
            var containerBounds = container.BoundingRectangle;
            var windowBounds = AutomationElement.FromHandle(_targetWindow).Current.BoundingRectangle;
            if (containerBounds.IsEmpty || windowBounds.IsEmpty ||
                containerBounds.Width < 120 || containerBounds.Height < 24 ||
                containerBounds.Width >= windowBounds.Width * 0.95 ||
                containerBounds.Height >= windowBounds.Height * 0.7 ||
                !containerBounds.Contains(point) ||
                point.X < containerBounds.Left + containerBounds.Width * 0.62 ||
                point.Y < containerBounds.Top + containerBounds.Height * 0.35)
                return false;

            var rightmostActionEdge = buttonBounds.Right;
            foreach (var sibling in EnumerateWindowElements(commonAncestor, 96, 8))
            {
                var siblingCurrent = sibling.Current;
                var siblingIsButton = siblingCurrent.ControlType == ControlType.Button ||
                                      siblingCurrent.ControlType == ControlType.Custom &&
                                      sibling.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                if (siblingIsButton && siblingCurrent.IsEnabled &&
                    !siblingCurrent.BoundingRectangle.IsEmpty)
                    rightmostActionEdge = Math.Max(rightmostActionEdge,
                        siblingCurrent.BoundingRectangle.Right);
            }

            return buttonBounds.Right >= rightmostActionEdge - Math.Max(36, buttonBounds.Width * 0.8);
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return false;
        }
    }

    private static AutomationElement? FindNearestCommonAncestor(
        AutomationElement left,
        AutomationElement right,
        int maxDepth,
        out int leftDepth,
        out int rightDepth)
    {
        leftDepth = int.MaxValue;
        rightDepth = int.MaxValue;
        var walker = TreeWalker.ControlViewWalker;
        var leftAncestors = new List<AutomationElement>();
        var rightAncestors = new List<AutomationElement>();
        try
        {
            var current = left;
            for (var depth = 0; current is not null && depth <= maxDepth; depth++)
            {
                leftAncestors.Add(current);
                current = walker.GetParent(current);
            }
            current = right;
            for (var depth = 0; current is not null && depth <= maxDepth; depth++)
            {
                rightAncestors.Add(current);
                current = walker.GetParent(current);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return null;
        }

        for (var leftIndex = 0; leftIndex < leftAncestors.Count; leftIndex++)
        {
            for (var rightIndex = 0; rightIndex < rightAncestors.Count; rightIndex++)
            {
                if (!AreSameAutomationElement(leftAncestors[leftIndex], rightAncestors[rightIndex])) continue;
                leftDepth = leftIndex;
                rightDepth = rightIndex;
                return leftAncestors[leftIndex];
            }
        }
        return null;
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WmLeftButtonDown &&
            Volatile.Read(ref _initialized) != 0 && Volatile.Read(ref _disposed) == 0)
        {
            var mouse = Marshal.PtrToStructure<MouseData>(lParam);
            var injected = (mouse.Flags & (LlMouseInjected | LlMouseLowerIntegrityInjected)) != 0;
            var targetForegroundMatched = GetForegroundWindow() == _targetWindow;
            if (!injected && targetForegroundMatched)
            {
                SubmitCandidate preparedCandidate;
                lock (_snapshotLock)
                    preparedCandidate = CreateCandidateLocked("mouse-send-button", DateTimeOffset.UtcNow);
                QueueAutomationNotice(new AutomationNotice(AutomationNoticeKind.MouseButtonDown, 0,
                    X: mouse.Point.X, Y: mouse.Point.Y,
                    TargetForegroundMatched: true, PreparedCandidate: preparedCandidate));
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0 &&
            wParam.ToInt32() is WmKeyUp or WmSysKeyUp)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (key.VirtualKey == VkReturn)
                Interlocked.Exchange(ref _enterKeyDown, 0);
        }
        else if (code >= 0 && Volatile.Read(ref _initialized) != 0 && Volatile.Read(ref _disposed) == 0 &&
                 wParam.ToInt32() is WmKeyDown or WmSysKeyDown)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (key.VirtualKey == VkReturn && Interlocked.Exchange(ref _enterKeyDown, 1) == 0)
            {
                var injected = (key.Flags & (LlkhfInjected | LlkhfLowerIntegrityInjected)) != 0;
                var targetForegroundMatched = GetForegroundWindow() == _targetWindow;
                var shift = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
                var control = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                var alt = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
                var trackedEditorFocused = targetForegroundMatched && WasTrackedEditorFocusedRecently();
                Log("enter_key_observed",
                    $"injected={injected};target-window={targetForegroundMatched};tracked-editor-focus={trackedEditorFocused};shift={shift};control={control};alt={alt}");

                var ignoredReason = injected ? "injected-key" :
                    !targetForegroundMatched ? "target-window-not-foreground" :
                    shift ? "shift-enter" : null;
                if (ignoredReason is null)
                    SignalSubmit("enter");
                else
                    Log("submit_signal_ignored", $"source=enter;reason={ignoredReason};control={control};alt={alt}");
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void SignalSubmit(string source, SubmitCandidate? preparedCandidate = null)
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
            else
            {
                var now = Stopwatch.GetTimestamp();
                var sameSignalWindow = now - _lastSignalTimestamp < Stopwatch.Frequency / 4;
                var candidateSnapshotVersion = preparedCandidate?.Snapshot.Version ?? _snapshot.Version;
                var duplicateButtonInvoke = IsButtonSubmitSource(source) &&
                                            IsButtonSubmitSource(_lastSignalSource) &&
                                            candidateSnapshotVersion == _lastSignalSnapshotVersion && sameSignalWindow;
                if (duplicateButtonInvoke)
                {
                    deduplicated = true;
                }
                else
                {
                    var captured = preparedCandidate is { IsValid: true }
                        ? preparedCandidate with { Source = source, SignalTime = DateTimeOffset.UtcNow }
                        : CreateCandidateLocked(source, DateTimeOffset.UtcNow);
                    if (!captured.IsValid)
                    {
                        ignoredReason = $"snapshot-not-fresh:{captured.Snapshot.State}";
                    }
                    else
                    {
                        if (_outgoingProofCandidate is { } outgoingProof)
                        {
                            ClearPendingSubmission(outgoingProof.Id);
                            _outgoingProofCandidate = null;
                        }
                        candidate = captured;
                        _latestCandidateId = candidate.Id;
                        _lastSignalSnapshotVersion = candidate.Snapshot.Version;
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
            if (ignoredReason.StartsWith("snapshot-not-fresh:", StringComparison.Ordinal))
                StatusChanged?.Invoke(this, new DictionaryLearningStatusUpdate(_monitorId,
                    "入力欄を再取得しています。送信後の確認はまだ行えていません。"));
            return;
        }
        if (deduplicated)
        {
            Interlocked.Increment(ref _deduplicatedSignalCount);
            return;
        }
        if (candidate is null) return;

        StatusChanged?.Invoke(this,
            new DictionaryLearningStatusUpdate(_monitorId, "送信後の変化を確認しています…"));
        EnqueueSubmission(candidate);
    }

    private void EnqueueSubmission(SubmitCandidate candidate)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _pendingSubmissionId, candidate.Id);
        Interlocked.Exchange(ref _pendingSubmissionTimestamp, Stopwatch.GetTimestamp());
        _pendingSubmissions.Enqueue(candidate);
        try
        {
            if (_monitorWakeSignal.CurrentCount == 0) _monitorWakeSignal.Release();
        }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private void QueueAutomationNotice(AutomationNotice notice)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _automationNotices.Enqueue(notice);
        try
        {
            if (_monitorWakeSignal.CurrentCount == 0) _monitorWakeSignal.Release();
        }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private bool TryCompleteUnsignaledSubmission(string trigger)
    {
        SubmitCandidate? candidate;
        var created = false;
        lock (_snapshotLock)
        {
            if (_submissionCompleted != 0) return true;
            if (_outgoingProofCandidate is not null)
            {
                candidate = _outgoingProofCandidate;
            }
            else
            {
                if (HasPendingSubmissionIntent()) return false;
                var snapshot = _lastValidSnapshot;
                if (snapshot.State != SnapshotState.Valid || string.IsNullOrWhiteSpace(snapshot.Segment) ||
                    GetSnapshotAgeMilliseconds(snapshot) > UnsignaledSnapshotMaxAgeMs)
                    return false;

                candidate = new SubmitCandidate(++_nextCandidateId, "outgoing-proof-fallback",
                    DateTimeOffset.UtcNow, snapshot, true, true);
                _outgoingProofCandidate = candidate;
                _latestCandidateId = candidate.Id;
                Interlocked.Exchange(ref _pendingSubmissionId, candidate.Id);
                Interlocked.Exchange(ref _pendingSubmissionTimestamp, Stopwatch.GetTimestamp());
                created = true;
            }

            var now = Stopwatch.GetTimestamp();
            var recoveryExpired = DateTimeOffset.UtcNow - candidate.SignalTime >=
                                  TimeSpan.FromMilliseconds(SubmissionRecoveryWindowMs);
            if (!recoveryExpired && _lastOutgoingProofCheckTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastOutgoingProofCheckTimestamp, now) < TimeSpan.FromMilliseconds(500))
                return false;
            if (!recoveryExpired && candidate.OutgoingProofAttempts >= MaxOutgoingProofAttemptsPerCandidate)
                return false;
            if (!recoveryExpired)
            {
                candidate = candidate with { OutgoingProofAttempts = candidate.OutgoingProofAttempts + 1 };
                _outgoingProofCandidate = candidate;
                _lastOutgoingProofCheckTimestamp = now;
            }
        }

        if (candidate is null) return false;
        if (created)
        {
            Log("outgoing_proof_candidate_started",
                $"trigger={trigger};{SnapshotDescription(candidate.Snapshot)}", 0, candidate.Id);
            StatusChanged?.Invoke(this,
                new DictionaryLearningStatusUpdate(_monitorId,
                    trigger == "tracked-range-lost"
                        ? "入力欄の変化を確認しています…"
                        : "送信後の表示を確認しています…"));
        }

        if (DateTimeOffset.UtcNow - candidate.SignalTime >= TimeSpan.FromMilliseconds(SubmissionRecoveryWindowMs))
        {
            lock (_snapshotLock)
            {
                if (_outgoingProofCandidate?.Id == candidate.Id)
                {
                    _outgoingProofCandidate = null;
                    ClearPendingSubmission(candidate.Id);
                }
            }
            ReportStatus("submission_not_confirmed", "送信後の変化を確認できず、自動登録を見送りました。",
                $"source=outgoing-proof-fallback;trigger={trigger};reason=no-new-matching-message", 0, candidate.Id);
            return false;
        }

        if (!HasNewOutgoingTextEvidence(candidate.Snapshot.FullText, out var evidence, requireExactMatch: true))
            return false;

        var correction = !string.Equals(candidate.Snapshot.Segment, _originalSegment, StringComparison.Ordinal);
        Log("submit_candidate_captured",
            $"source=outgoing-proof-fallback;valid=True;recent-preclear=True;{SnapshotDescription(candidate.Snapshot)}",
            0, candidate.Id);
        CompleteConfirmedSubmission(candidate, correction,
            $"composer-cleared;{evidence}", "outgoing-message-proof", 1);
        lock (_snapshotLock)
        {
            if (_outgoingProofCandidate?.Id == candidate.Id)
                _outgoingProofCandidate = null;
        }
        return Volatile.Read(ref _submissionCompleted) != 0;
    }

    private void ClearUnsignaledSubmissionCandidate()
    {
        SubmitCandidate? candidate;
        lock (_snapshotLock)
        {
            candidate = _outgoingProofCandidate;
            _outgoingProofCandidate = null;
            if (candidate is not null) ClearPendingSubmission(candidate.Id);
        }
        if (candidate is not null)
            Log("outgoing_proof_candidate_cancelled", "reason=composer-no-longer-empty", 0, candidate.Id);
    }

    private void LogMouseHitTestMiss(string result)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastMouseMissDiagnosticTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return;
        if (Interlocked.CompareExchange(ref _lastMouseMissDiagnosticTimestamp, now, previous) == previous)
            Log("mouse_click_not_send_button", $"target-window=true;hit-test={result}");
    }

    private async Task<bool> ProcessSubmissionCandidateAsync(
        SubmitCandidate candidate,
        Func<EditorAccess?> getEditorAccess,
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
            ClearPendingSubmission(candidate.Id);
            return false;
        }

        var correction = !string.Equals(snapshot.Segment, _originalSegment, StringComparison.Ordinal);
        var confirmationReadCount = 0;
        var confirmationReadFailureCount = 0;
        var observedChangedText = false;
        var observedClearedText = false;
        var clearEvidence = "none";
        var outgoingProofAttempts = 0;
        var editorRebindAttempts = 0;
        var recoveryDeadline = candidate.SignalTime + TimeSpan.FromMilliseconds(SubmissionRecoveryWindowMs);

        bool TryRebindOnce()
        {
            if (editorRebindAttempts >= 1) return false;
            editorRebindAttempts++;
            return tryRebindEditor();
        }

        bool TryConfirmFromOutgoingMessage(string reason, int attempt)
        {
            if (attempt % 3 != 2 || DateTimeOffset.UtcNow >= recoveryDeadline) return false;
            if (outgoingProofAttempts >= MaxOutgoingProofAttemptsPerCandidate)
            {
                clearEvidence = $"{reason};outgoing-proof-attempt-limit";
                return false;
            }
            outgoingProofAttempts++;
            if (!HasNewOutgoingTextEvidence(snapshot.FullText, out var outgoingEvidence))
            {
                clearEvidence = $"{reason};{outgoingEvidence}";
                return false;
            }

            CompleteConfirmedSubmission(candidate, correction,
                $"{reason};{outgoingEvidence}", "outgoing-message-proof", attempt);
            return true;
        }

        for (var confirmAttempt = 0; confirmAttempt < 16; confirmAttempt++)
        {
            var remaining = recoveryDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(500)
                ? remaining
                : TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow >= recoveryDeadline) break;
            if (candidate.Id != Volatile.Read(ref _latestCandidateId))
            {
                Log("submit_candidate_superseded", $"source={candidate.Source};newer-candidate-exists",
                    confirmAttempt, candidate.Id);
                return false;
            }
            var editorAccess = getEditorAccess();
            if (editorAccess is null || !IsSameTrackedEditor(editorAccess.Element))
            {
                if (TryConfirmFromOutgoingMessage("editor-unavailable", confirmAttempt)) return false;
                if (!TryRebindOnce())
                {
                    confirmationReadFailureCount++;
                    Log("submit_confirmation_editor_mismatch", "reason=rebind-unavailable",
                        confirmationReadFailureCount, candidate.Id);
                    continue;
                }
                editorAccess = getEditorAccess();
                if (editorAccess is null || !IsSameTrackedEditor(editorAccess.Element))
                {
                    if (TryConfirmFromOutgoingMessage("editor-identity-changed", confirmAttempt)) return false;
                    confirmationReadFailureCount++;
                    Log("submit_confirmation_editor_mismatch", "reason=target-or-editor-identity-changed",
                        confirmationReadFailureCount, candidate.Id);
                    continue;
                }
            }
            if (!TryReadText(editorAccess.Element, editorAccess.TextPattern, editorAccess.ValuePattern,
                    out var afterSubmit, out var readState))
            {
                if (TryConfirmFromOutgoingMessage("editor-unreadable", confirmAttempt)) return false;
                if (TryRebindOnce())
                {
                    editorAccess = getEditorAccess();
                    if (editorAccess is null || !IsSameTrackedEditor(editorAccess.Element))
                    {
                        if (TryConfirmFromOutgoingMessage("editor-identity-changed", confirmAttempt)) return false;
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
                    readState = $"rebound-same-editor;{reboundReadState}";
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
                ClearPendingSubmission(candidate.Id);
                return false;
            }

            var editorChanged = !string.Equals(afterSubmit, snapshot.FullText, StringComparison.Ordinal);
            var contentCleared = IsTrackedRangeCleared(afterSubmit, out var evidence);
            observedChangedText |= editorChanged;
            observedClearedText |= contentCleared;
            if (!editorChanged || !contentCleared)
            {
                var stateReason = !editorChanged ? "field-unchanged" : "tracked-range-not-cleared";
                if (TryConfirmFromOutgoingMessage(stateReason, confirmationReadCount)) return false;
                continue;
            }

            var editorWasRecreated = snapshot.EditorRuntimeId is null || editorAccess.RuntimeId is null ||
                                     !string.Equals(snapshot.EditorRuntimeId, editorAccess.RuntimeId,
                                         StringComparison.Ordinal);
            var requiresOutgoingTextEvidence = candidate.Source == "mouse-composer-button" || editorWasRecreated ||
                                               string.Equals(_trackedEditorIdentity?.FrameworkId, "Chrome",
                                                   StringComparison.OrdinalIgnoreCase);
            if (requiresOutgoingTextEvidence)
            {
                if (TryConfirmFromOutgoingMessage("editor-recreated", confirmAttempt)) return false;
                if (confirmAttempt % 3 == 2)
                    Log("submit_confirmation_outgoing_text_missing", clearEvidence,
                        confirmationReadCount, candidate.Id);
                continue;
            }

            clearEvidence = evidence;
            CompleteConfirmedSubmission(candidate, correction, clearEvidence, readState, confirmationReadCount);
            return false;
        }

        var reason = confirmationReadCount == 0
            ? "editor-unreadable-or-identity-changed"
            : !observedChangedText
                ? "field-unchanged"
                : !observedClearedText
                    ? "tracked-text-retained-or-range-lost"
                    : "inconsistent-editor-state";
        var finalEditorAccess = getEditorAccess();
        var providerState = finalEditorAccess is null ? "editor=unbound" : "editor=bound";
        ReportStatus("submission_not_confirmed", "送信後の入力欄の変化を確認できず、登録を見送りました。",
            $"candidate={candidate.Id};source={candidate.Source};reason={reason};{SnapshotDescription(snapshot)};{providerState};last-read={clearEvidence}",
            confirmationReadCount + confirmationReadFailureCount, candidate.Id);
        ClearPendingSubmission(candidate.Id);
        return false;
    }

    private void CompleteConfirmedSubmission(
        SubmitCandidate candidate,
        bool correction,
        string evidence,
        string readState,
        int confirmationReadCount)
    {
        var accepted = false;
        lock (_snapshotLock)
        {
            if (_submissionCompleted == 0 && _latestCandidateId == candidate.Id)
            {
                _submissionCompleted = 1;
                ClearPendingSubmission(candidate.Id);
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
            $"source={candidate.Source};evidence={evidence};snapshot={SnapshotDescription(candidate.Snapshot)};read={readState}",
            confirmationReadCount, candidate.Id);
        if (!correction)
        {
            ReportStatus("submitted_without_correction", "手修正が見つからなかったため、辞書は変更していません。",
                $"candidate={candidate.Id};evidence={evidence}", confirmationReadCount, candidate.Id);
            return;
        }

        ReportStatus("submission_ready_for_learning", "送信を確認しました。修正候補を判定しています。",
            $"candidate={candidate.Id};evidence={evidence}", confirmationReadCount, candidate.Id);
        SubmissionConfirmed?.Invoke(this,
            new DictionaryCorrectionSubmission(_originalSegment, candidate.Snapshot.Segment, _monitorId, candidate.Id));
    }

    private bool HasPendingSubmissionIntent()
    {
        if (Interlocked.Read(ref _pendingSubmissionId) == 0) return false;
        var timestamp = Interlocked.Read(ref _pendingSubmissionTimestamp);
        return timestamp != 0 &&
               Stopwatch.GetElapsedTime(timestamp) <= TimeSpan.FromMilliseconds(SubmissionRecoveryWindowMs);
    }

    private void ClearPendingSubmission(long candidateId)
    {
        if (Interlocked.CompareExchange(ref _pendingSubmissionId, 0, candidateId) == candidateId)
            Interlocked.Exchange(ref _pendingSubmissionTimestamp, 0);
    }

    private void ReportStatus(string stage, string message, string detail = "none", int count = 0,
        long? candidateId = null)
    {
        Log(stage, detail, count, candidateId);
        StatusChanged?.Invoke(this, new DictionaryLearningStatusUpdate(_monitorId, message));
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

    private void SetSnapshot(string fullText, string segment)
    {
        lock (_snapshotLock)
            SetSnapshotLocked(fullText, segment,
                string.IsNullOrWhiteSpace(segment) ? SnapshotState.EmptySegment : SnapshotState.Valid,
                string.IsNullOrWhiteSpace(segment) ? "tracked-segment-empty" : "readable");
    }

    private EditorSnapshot GetSnapshot()
    {
        lock (_snapshotLock) return _snapshot;
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
        var binding = Volatile.Read(ref _editorBinding);
        var editorRuntimeId = binding?.RuntimeId;
        var documentRuntimeId = binding?.Fingerprint.DocumentRuntimeId;
        var editorGeneration = binding?.Generation ?? 0;
        var contentChanged = _snapshot.State != state || _snapshot.ReadState != readState ||
                             !string.Equals(_snapshot.FullText, fullText, StringComparison.Ordinal) ||
                             !string.Equals(_snapshot.Segment, segment, StringComparison.Ordinal) ||
                             !string.Equals(_snapshot.EditorRuntimeId, editorRuntimeId, StringComparison.Ordinal) ||
                             !string.Equals(_snapshot.DocumentRuntimeId, documentRuntimeId, StringComparison.Ordinal) ||
                             _snapshot.EditorGeneration != editorGeneration;
        var version = contentChanged ? ++_snapshotVersion : _snapshot.Version;
        _snapshot = new EditorSnapshot(version, Stopwatch.GetTimestamp(), fullText, segment, state, readState,
            editorRuntimeId, documentRuntimeId, editorGeneration);
        if (state == SnapshotState.Valid) _lastValidSnapshot = _snapshot;
    }

    private static bool IsButtonSubmitSource(string source) =>
        source is "send-button" or "mouse-send-button" or "mouse-composer-button";

    private SubmitCandidate CreateCandidateLocked(string source, DateTimeOffset signalTime)
    {
        var snapshot = _snapshot;
        var usedRecentPreClearSnapshot = false;
        var currentFieldCleared = snapshot.State == SnapshotState.EmptySegment ||
                                  snapshot.State == SnapshotState.RangeLost &&
                                  (string.IsNullOrWhiteSpace(snapshot.FullText) ||
                                   IsComposerPlaceholder(snapshot.FullText));
        if (IsButtonSubmitSource(source) && currentFieldCleared &&
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
        $"version={snapshot.Version};state={snapshot.State};age-ms={GetSnapshotAgeMilliseconds(snapshot)};full-length={snapshot.FullText.Length};segment-length={snapshot.Segment.Length};editor-generation={snapshot.EditorGeneration};read={snapshot.ReadState}";

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
        if (IsComposerPlaceholder(fullText))
        {
            evidence = "same-editor-composer-placeholder";
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
        var expected = _trackedEditorIdentity;
        var current = CaptureEditorFingerprint(editor);
        var binding = Volatile.Read(ref _editorBinding);
        return expected is not null && current is not null &&
               IsWithinDocumentScope(editor, expected.DocumentRuntimeId, binding?.DocumentRoot) &&
               TryMatchEditorFingerprint(expected, current, out _, out _);
    }

    private static bool IsWithinDocumentScope(
        AutomationElement element,
        string? expectedDocumentRuntimeId,
        AutomationElement? documentRoot)
    {
        if (expectedDocumentRuntimeId is not null)
            return string.Equals(GetDocumentRuntimeId(element), expectedDocumentRuntimeId, StringComparison.Ordinal);
        if (documentRoot is null) return false;

        var walker = TreeWalker.ControlViewWalker;
        try
        {
            var current = walker.GetParent(element);
            for (var depth = 0; current is not null && depth < 16; depth++)
            {
                if (Automation.Compare(current, documentRoot)) return true;
                current = walker.GetParent(current);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return false;
    }

    private IReadOnlyList<EditorCandidate> FindMatchingEditorCandidates(
        IntPtr targetWindow,
        EditorFingerprint expected,
        AutomationElement? trackedDocumentRoot,
        string previousText,
        bool allowClearedText,
        out int examined,
        out Dictionary<string, int> rejectionCounts,
        out bool focusedEditorFound)
    {
        examined = 0;
        focusedEditorFound = false;
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        rejectionCounts = rejections;
        var matches = new List<EditorCandidate>();
        var visitedIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedNoId = new HashSet<int>();
        var elements = new List<(AutomationElement Element, TextPattern? Text, ValuePattern? Value)>();

        void Add(AutomationElement element, TextPattern? textPattern, ValuePattern? valuePattern)
        {
            string? runtimeId = GetRuntimeId(element);
            if (runtimeId is not null)
            {
                if (!visitedIds.Add(runtimeId)) return;
            }
            else if (!visitedNoId.Add(element.GetHashCode()))
            {
                return;
            }
            elements.Add((element, textPattern, valuePattern));
        }

        var focused = FindFocusedEditor(targetWindow, out var focusedText, out var focusedValue);
        if (focused is not null)
        {
            focusedEditorFound = true;
            Add(focused, focusedText, focusedValue);
        }

        var hasDocumentScope = trackedDocumentRoot is not null || expected.DocumentRuntimeId is not null;
        if (trackedDocumentRoot is not null || expected.DocumentRuntimeId is not null)
        {
            try
            {
                var root = trackedDocumentRoot ?? AutomationElement.FromHandle(targetWindow);
                foreach (var element in EnumerateWindowElements(root, MaxAutomationSearchNodes,
                             MaxAutomationTreeDepth))
                {
                    examined++;
                    if (!TryGetEditableTextPatterns(element, out var textPattern, out var valuePattern)) continue;
                    Add(element, textPattern, valuePattern);
                }
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
            {
                rejections[trackedDocumentRoot is null ? "window-tree-unavailable" : "document-scope-unavailable"] = 1;
            }
        }
        else if (focused is not null)
            rejections["document-anchor-unavailable"] = 1;

        foreach (var candidate in elements)
        {
            if (!IsWithinWindow(candidate.Element, targetWindow))
            {
                Increment("outside-target-window");
                continue;
            }
            if (!IsWithinDocumentScope(candidate.Element, expected.DocumentRuntimeId, trackedDocumentRoot))
            {
                Increment("document-scope-mismatch");
                continue;
            }
            var fingerprint = CaptureEditorFingerprint(candidate.Element);
            if (fingerprint is null)
            {
                Increment("fingerprint-unavailable");
                continue;
            }
            var sameRuntimeId = expected.RuntimeId is not null &&
                                string.Equals(expected.RuntimeId, fingerprint.RuntimeId, StringComparison.Ordinal);
            if (!hasDocumentScope && !sameRuntimeId)
            {
                Increment("document-anchor-unavailable");
                continue;
            }
            if (!TryMatchEditorFingerprint(expected, fingerprint, out var score, out var reason))
            {
                Increment(reason);
                continue;
            }
            if (!sameRuntimeId)
            {
                if (!TryReadText(candidate.Element, candidate.Text, candidate.Value,
                        out var currentText, out var candidateReadState))
                {
                    Increment($"candidate-text-unreadable:{candidateReadState}");
                    continue;
                }
                currentText = NormalizeLineEndings(currentText);
                var continuity = HasMeaningfulTextContinuity(previousText, currentText);
                var isExpectedClear = allowClearedText &&
                                      (string.IsNullOrWhiteSpace(currentText) || IsComposerPlaceholder(currentText));
                if (!continuity && !isExpectedClear)
                {
                    Increment("content-continuity-mismatch");
                    continue;
                }
                if (continuity) score += 40;
            }
            var isFocused = focused is not null &&
                            string.Equals(GetRuntimeId(focused), fingerprint.RuntimeId, StringComparison.Ordinal);
            matches.Add(new EditorCandidate(candidate.Element, candidate.Text, candidate.Value, fingerprint,
                score, isFocused));
        }
        return matches;

        void Increment(string reason)
        {
            rejections.TryGetValue(reason, out var count);
            rejections[reason] = count + 1;
        }
    }

    private bool HasMeaningfulTextContinuity(string previousText, string currentText)
    {
        previousText = NormalizeLineEndings(previousText);
        currentText = NormalizeLineEndings(currentText);
        if (previousText.Length == 0 || currentText.Length == 0) return false;
        if (string.Equals(previousText, currentText, StringComparison.Ordinal))
            return true;
        var shorterLength = Math.Min(previousText.Length, currentText.Length);
        if (shorterLength < 4) return false;
        if (previousText.Contains(currentText, StringComparison.Ordinal) ||
            currentText.Contains(previousText, StringComparison.Ordinal))
            return true;

        var stablePrefix = 0;
        while (stablePrefix < previousText.Length && stablePrefix < currentText.Length &&
               previousText[stablePrefix] == currentText[stablePrefix])
            stablePrefix++;

        var stableSuffix = 0;
        while (stableSuffix < previousText.Length - stablePrefix &&
               stableSuffix < currentText.Length - stablePrefix &&
               previousText[^(stableSuffix + 1)] == currentText[^(stableSuffix + 1)])
            stableSuffix++;

        var sharedContext = stablePrefix + stableSuffix;
        var requiredContext = Math.Min(12, Math.Max(3, shorterLength / 3));
        return sharedContext >= requiredContext;
    }

    private static string FormatRejectionCounts(IReadOnlyDictionary<string, int> rejectionCounts) =>
        rejectionCounts.Count == 0
            ? "none"
            : string.Join(",", rejectionCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}"));

    private static bool TryMatchEditorFingerprint(
        EditorFingerprint expected,
        EditorFingerprint current,
        out int score,
        out string reason)
    {
        score = 0;
        if (current.ProcessId != expected.ProcessId)
        {
            reason = "process-id-mismatch";
            return false;
        }
        if (current.ControlTypeId != expected.ControlTypeId)
        {
            reason = "control-type-mismatch";
            return false;
        }
        if (expected.FrameworkId.Length > 0 &&
            !string.Equals(expected.FrameworkId, current.FrameworkId, StringComparison.Ordinal))
        {
            reason = "framework-mismatch";
            return false;
        }
        if (expected.DocumentRuntimeId is not null &&
            !string.Equals(expected.DocumentRuntimeId, current.DocumentRuntimeId, StringComparison.Ordinal))
        {
            reason = "document-anchor-mismatch";
            return false;
        }

        var sameRuntimeId = expected.RuntimeId is not null &&
                            string.Equals(expected.RuntimeId, current.RuntimeId, StringComparison.Ordinal);
        var sameAutomationId = expected.StableAutomationId.Length > 0 &&
                               string.Equals(expected.StableAutomationId, current.StableAutomationId,
                                   StringComparison.Ordinal);
        var sharedClassTokenCount = expected.StableClassTokens.Intersect(current.StableClassTokens,
            StringComparer.OrdinalIgnoreCase).Count();
        var ancestorMatches = CountOrderedAncestorMatches(expected.Ancestors, current.Ancestors);
        if (!sameRuntimeId && !sameAutomationId && sharedClassTokenCount == 0)
        {
            reason = "editor-class-token-mismatch";
            return false;
        }
        if (!sameRuntimeId && expected.DocumentRuntimeId is null &&
            !(sameAutomationId || ancestorMatches >= 2))
        {
            reason = "ancestor-structure-mismatch";
            return false;
        }

        score = (sameRuntimeId ? 10_000 : 0) +
                (expected.DocumentRuntimeId is not null ? 100 : 0) +
                (sameAutomationId ? 50 : 0) +
                (sharedClassTokenCount * 20) +
                (ancestorMatches * 8) +
                GetBoundsProximityScore(expected.Bounds, current.Bounds);
        reason = "matched";
        return true;
    }

    private static int CountOrderedAncestorMatches(
        IReadOnlyList<EditorNodeFingerprint> expected,
        IReadOnlyList<EditorNodeFingerprint> current)
    {
        var matches = 0;
        var currentStart = 0;
        foreach (var expectedNode in expected)
        {
            for (var index = currentStart; index < current.Count; index++)
            {
                var currentNode = current[index];
                if (expectedNode.ControlTypeId != currentNode.ControlTypeId) continue;
                if (expectedNode.StableAutomationId.Length > 0 &&
                    string.Equals(expectedNode.StableAutomationId, currentNode.StableAutomationId,
                        StringComparison.Ordinal))
                {
                    matches++;
                    currentStart = index + 1;
                    break;
                }
                if (expectedNode.FrameworkId.Length > 0 &&
                    !string.Equals(expectedNode.FrameworkId, currentNode.FrameworkId, StringComparison.Ordinal))
                    continue;
                if (!expectedNode.StableClassTokens.Intersect(currentNode.StableClassTokens,
                        StringComparer.OrdinalIgnoreCase).Any())
                    continue;
                matches++;
                currentStart = index + 1;
                break;
            }
        }
        return matches;
    }

    private static EditorFingerprint? CaptureEditorFingerprint(AutomationElement editor)
    {
        try
        {
            var current = editor.Current;
            var ancestors = new List<EditorNodeFingerprint>(8);
            string? documentRuntimeId = null;
            var walker = TreeWalker.ControlViewWalker;
            var parent = walker.GetParent(editor);
            for (var depth = 0; parent is not null && depth < 16; depth++)
            {
                var parentInfo = parent.Current;
                var parentRuntimeId = GetRuntimeId(parent);
                ancestors.Add(new EditorNodeFingerprint(parentInfo.ControlType.Id,
                    parentInfo.FrameworkId ?? string.Empty, GetStableAutomationId(parentInfo.AutomationId),
                    GetStableClassTokens(parentInfo.ClassName), parentRuntimeId));
                if (parentInfo.ControlType == ControlType.Document && documentRuntimeId is null)
                    documentRuntimeId = parentRuntimeId;
                parent = walker.GetParent(parent);
            }

            return new EditorFingerprint(current.ProcessId, current.ControlType.Id,
                current.FrameworkId ?? string.Empty, GetStableAutomationId(current.AutomationId),
                GetStableClassTokens(current.ClassName), GetRuntimeId(editor), documentRuntimeId,
                ancestors.ToArray(), current.BoundingRectangle);
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return null;
        }
    }

    private static string? GetDocumentRuntimeId(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        try
        {
            var parent = walker.GetParent(element);
            for (var depth = 0; parent is not null && depth < 16; depth++)
            {
                if (parent.Current.ControlType == ControlType.Document)
                    return GetRuntimeId(parent);
                parent = walker.GetParent(parent);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return null;
    }

    private static int GetBoundsProximityScore(Rect original, Rect current)
    {
        if (original.IsEmpty || current.IsEmpty || original.Width <= 0 || original.Height <= 0 ||
            current.Width <= 0 || current.Height <= 0)
            return 0;
        if (original.IntersectsWith(current)) return 5;
        var centerXDistance = Math.Abs(original.Left + original.Width / 2 - current.Left - current.Width / 2);
        var centerYDistance = Math.Abs(original.Top + original.Height / 2 - current.Top - current.Height / 2);
        return centerXDistance <= Math.Max(300, Math.Max(original.Width, current.Width)) &&
               centerYDistance <= Math.Max(240, Math.Max(original.Height, current.Height) * 2)
            ? 1
            : 0;
    }

    private static string DescribeBoundsEvidence(Rect original, Rect current)
    {
        if (original.IsEmpty || current.IsEmpty || original.Width <= 0 || original.Height <= 0 ||
            current.Width <= 0 || current.Height <= 0)
            return "unavailable";
        if (original.IntersectsWith(current)) return "overlap";
        return GetBoundsProximityScore(original, current) > 0 ? "near" : "far";
    }

    private static string GetStableAutomationId(string? automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 64 ||
            Guid.TryParse(automationId, out _) || automationId.Any(char.IsWhiteSpace))
            return string.Empty;
        var compact = automationId.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        if (compact.Length >= 16 && compact.All(Uri.IsHexDigit)) return string.Empty;
        return automationId;
    }

    private static string[] GetStableClassTokens(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return Array.Empty<string>();
        return className.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length <= 80 &&
                            !token.Contains("focus", StringComparison.OrdinalIgnoreCase) &&
                            !token.Contains("active", StringComparison.OrdinalIgnoreCase) &&
                            !Guid.TryParse(token, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static EditorBinding? CreateEditorBinding(
        AutomationElement element,
        TextPattern? textPattern,
        ValuePattern? valuePattern,
        long generation,
        EditorFingerprint? fingerprint = null)
    {
        fingerprint ??= CaptureEditorFingerprint(element);
        return fingerprint is null
            ? null
            : new EditorBinding(element, textPattern, valuePattern, fingerprint.RuntimeId, fingerprint,
                FindDocumentAncestor(element), generation);
    }

    private static AutomationElement? FindDocumentAncestor(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        try
        {
            var parent = walker.GetParent(element);
            for (var depth = 0; parent is not null && depth < 16; depth++)
            {
                if (parent.Current.ControlType == ControlType.Document) return parent;
                parent = walker.GetParent(parent);
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return null;
    }

    private static IReadOnlyList<AutomationElement> EnumerateWindowElements(
        AutomationElement root,
        int maximumNodes,
        int maximumDepth,
        AutomationElement? excludedSubtree = null)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        var result = new List<AutomationElement>(Math.Min(maximumNodes, 512));
        try
        {
            var firstChild = walker.GetFirstChild(root);
            var siblings = new List<AutomationElement>();
            for (var child = firstChild; child is not null && siblings.Count < maximumNodes;)
            {
                siblings.Add(child);
                try { child = walker.GetNextSibling(child); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { break; }
            }
            for (var index = siblings.Count - 1; index >= 0; index--)
                pending.Push((siblings[index], 1));
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return result;
        }

        while (pending.Count > 0 && result.Count < maximumNodes)
        {
            var (element, depth) = pending.Pop();
            if (excludedSubtree is not null && AreSameAutomationElement(element, excludedSubtree)) continue;
            result.Add(element);
            if (depth >= maximumDepth) continue;
            try
            {
                var child = walker.GetFirstChild(element);
                var siblings = new List<AutomationElement>();
                while (child is not null && result.Count + pending.Count + siblings.Count < maximumNodes)
                {
                    siblings.Add(child);
                    try { child = walker.GetNextSibling(child); }
                    catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { break; }
                }
                for (var index = siblings.Count - 1; index >= 0; index--)
                    pending.Push((siblings[index], depth + 1));
            }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        }
        return result;
    }

    private static bool TryGetEditableTextPatterns(
        AutomationElement element,
        out TextPattern? textPattern,
        out ValuePattern? valuePattern)
    {
        textPattern = null;
        valuePattern = null;
        try
        {
            var current = element.Current;
            if (current.IsPassword || !current.IsEnabled || current.IsOffscreen) return false;
            var hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out var textValue) &&
                          textValue is TextPattern;
            var hasEditableValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                                   value is ValuePattern pattern && !pattern.Current.IsReadOnly;
            if (!hasText || (!hasEditableValue && current.ControlType != ControlType.Edit)) return false;
            textPattern = (TextPattern)textValue!;
            valuePattern = hasEditableValue ? (ValuePattern)value! : null;
            return true;
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> CaptureReadOnlyTextElementDigests(
        AutomationElement? documentRoot,
        string? trackedEditorRuntimeId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (documentRoot is null) return result;
        try
        {
            var rootCurrent = documentRoot.Current;
            if (rootCurrent.ControlType == ControlType.Document)
            {
                var rootRuntimeId = GetRuntimeId(documentRoot);
                var rootText = ReadRenderedTextForEvidence(documentRoot, rootCurrent);
                if (rootRuntimeId is not null && rootText.Length <= MaxDocumentLength &&
                    !string.Equals(rootRuntimeId, trackedEditorRuntimeId, StringComparison.Ordinal))
                    result[rootRuntimeId] = ComputeTextDigest(rootText);
            }

            foreach (var element in EnumerateWindowElements(documentRoot, MaxAutomationSearchNodes,
                         MaxAutomationTreeDepth))
            {
                try
                {
                    var current = element.Current;
                    if (current.ControlType != ControlType.Text &&
                        current.ControlType != ControlType.Group &&
                        current.ControlType != ControlType.ListItem &&
                        current.ControlType != ControlType.Custom &&
                        current.ControlType != ControlType.Document)
                        continue;
                    var runtimeId = GetRuntimeId(element);
                    if (runtimeId is null || string.Equals(runtimeId, trackedEditorRuntimeId, StringComparison.Ordinal))
                        continue;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                        value is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly)
                        continue;
                    var renderedText = ReadRenderedTextForEvidence(element, current);
                    if (renderedText.Length <= MaxDocumentLength)
                        result[runtimeId] = ComputeTextDigest(renderedText);
                }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return result;
    }

    private Dictionary<string, string> CaptureInitialReadOnlyTextElementDigests(EditorBinding binding)
    {
        var result = CaptureReadOnlyTextElementDigests(binding.DocumentRoot, binding.RuntimeId);
        try
        {
            var windowRoot = AutomationElement.FromHandle(_targetWindow);
            foreach (var pair in CaptureReadOnlyTextElementDigests(windowRoot, binding.RuntimeId))
                result.TryAdd(pair.Key, pair.Value);
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        return result;
    }

    private bool HasNewOutgoingTextEvidence(string expectedText, out string evidence, bool requireExactMatch = false)
    {
        var normalizedExpected = NormalizePlaceholderText(expectedText);
        var currentBinding = Volatile.Read(ref _editorBinding);
        var baselineDigests = Volatile.Read(ref _baselineTextElementDigests);
        if (normalizedExpected.Length < 2)
        {
            evidence = "submitted-text-too-short";
            return false;
        }
        if (baselineDigests.Count == 0)
        {
            evidence = "outgoing-baseline-unavailable";
            return false;
        }

        var roots = new List<(AutomationElement Element, bool IsOriginalDocument)>(2);
        if (currentBinding?.DocumentRoot is { } documentRoot)
            roots.Add((documentRoot, true));
        try
        {
            var windowRoot = AutomationElement.FromHandle(_targetWindow);
            if (!roots.Any(root => AreSameAutomationElement(root.Element, windowRoot)))
                roots.Add((windowRoot, false));
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        if (roots.Count == 0)
        {
            evidence = "tracked-window-anchor-unavailable";
            return false;
        }

        var examinedNodes = 0;
        var changedReadOnlyNodes = 0;
        var changedDocumentRoots = 0;
        var rootsVisited = 0;
        try
        {
            foreach (var root in roots)
            {
                rootsVisited++;
                if (root.IsOriginalDocument)
                {
                    try
                    {
                        var rootCurrent = root.Element.Current;
                        var rootRuntimeId = GetRuntimeId(root.Element);
                        if (rootCurrent.ControlType == ControlType.Document && rootRuntimeId is not null &&
                            baselineDigests.TryGetValue(rootRuntimeId, out var baselineRootDigest))
                        {
                            var rootText = ReadRenderedTextForEvidence(root.Element, rootCurrent);
                            if (rootText.Length <= MaxDocumentLength &&
                                !string.Equals(ComputeTextDigest(rootText), baselineRootDigest, StringComparison.Ordinal))
                                changedDocumentRoots++;
                        }
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
                }

                var rootBudget = roots.Count > 1 && !root.IsOriginalDocument
                    ? MaxOutgoingProofNodes / 3
                    : MaxOutgoingProofNodes;
                var excludedSubtree = root.IsOriginalDocument ? null : currentBinding?.DocumentRoot;
                foreach (var element in EnumerateWindowElements(root.Element, rootBudget,
                             MaxAutomationTreeDepth, excludedSubtree))
                {
                    examinedNodes++;
                    try
                    {
                        if (!IsWithinWindow(element, _targetWindow)) continue;
                        var trackedIdentity = _trackedEditorIdentity;
                        var inOriginalDocument = IsWithinDocumentScope(element,
                            trackedIdentity?.DocumentRuntimeId, currentBinding?.DocumentRoot);
                        if (root.IsOriginalDocument && !inOriginalDocument)
                            continue;
                        if (!root.IsOriginalDocument && inOriginalDocument)
                            continue;
                        var current = element.Current;
                        if (current.IsOffscreen) continue;
                        if (current.ControlType != ControlType.Text &&
                            current.ControlType != ControlType.Group &&
                            current.ControlType != ControlType.ListItem &&
                            current.ControlType != ControlType.Custom)
                            continue;
                        var runtimeId = GetRuntimeId(element);
                        if (runtimeId is null ||
                            string.Equals(runtimeId, currentBinding?.RuntimeId, StringComparison.Ordinal))
                            continue;
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                            value is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly)
                            continue;

                        var renderedText = ReadRenderedTextForEvidence(element, current);
                        if (renderedText.Length > MaxDocumentLength) continue;
                        var digest = ComputeTextDigest(renderedText);
                        if (baselineDigests.TryGetValue(runtimeId, out var baselineDigest) &&
                            string.Equals(digest, baselineDigest, StringComparison.Ordinal))
                            continue;
                        changedReadOnlyNodes++;
                        var normalizedRendered = NormalizePlaceholderText(renderedText);
                        var matchesSubmittedText = root.IsOriginalDocument
                            ? requireExactMatch
                                ? string.Equals(normalizedRendered, normalizedExpected,
                                    StringComparison.OrdinalIgnoreCase)
                                : ContainsSubmittedText(renderedText, normalizedExpected)
                            : string.Equals(normalizedRendered, normalizedExpected,
                                StringComparison.OrdinalIgnoreCase);
                        if (matchesSubmittedText)
                        {
                            evidence = root.IsOriginalDocument
                                ? requireExactMatch
                                    ? "new-readonly-text-node-exact-match-in-original-document"
                                    : "new-readonly-text-node-contains-submitted-correction"
                                : "new-readonly-text-node-exact-match-in-target-window";
                            return true;
                        }
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
                }
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }

        evidence = $"no-new-exact-outgoing-text-node;roots={rootsVisited};nodes={examinedNodes};changed-readonly={changedReadOnlyNodes};document-root-changed={changedDocumentRoots}";
        return false;
    }

    private static string ReadRenderedTextForEvidence(
        AutomationElement element,
        AutomationElement.AutomationElementInformation current)
    {
        var name = current.Name ?? string.Empty;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textValue) ||
            textValue is not TextPattern textPattern)
            return name;
        try
        {
            var text = textPattern.DocumentRange.GetText(MaxDocumentLength + 1);
            if (string.IsNullOrWhiteSpace(name)) return text;
            if (string.IsNullOrWhiteSpace(text) ||
                NormalizePlaceholderText(name).Contains(NormalizePlaceholderText(text), StringComparison.Ordinal))
                return name;
            return $"{name}\n{text}";
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { return name; }
    }

    private static bool ContainsSubmittedText(string renderedText, string normalizedExpected)
    {
        var normalizedRendered = NormalizePlaceholderText(renderedText);
        if (string.Equals(normalizedRendered, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            return true;
        return normalizedExpected.Length >= 12 &&
               normalizedRendered.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeTextDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeLineEndings(value))));

    private static bool AreSameAutomationElement(AutomationElement left, AutomationElement right)
    {
        try { return Automation.Compare(left, right); }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { return false; }
    }

    private static bool IsComposerPlaceholder(string fullText)
    {
        if (fullText.Length > MaxPlaceholderLength) return false;
        return KnownComposerPlaceholders.Contains(NormalizePlaceholderText(fullText));
    }

    private static bool IsComposerCleared(string fullText) =>
        string.IsNullOrWhiteSpace(fullText) || IsComposerPlaceholder(fullText);

    private static string NormalizePlaceholderText(string value) =>
        string.Join(" ", NormalizeLineEndings(value).Replace('\u00a0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record EditorNodeFingerprint(
        int ControlTypeId,
        string FrameworkId,
        string StableAutomationId,
        string[] StableClassTokens,
        string? RuntimeId);

    private sealed record EditorFingerprint(
        int ProcessId,
        int ControlTypeId,
        string FrameworkId,
        string StableAutomationId,
        string[] StableClassTokens,
        string? RuntimeId,
        string? DocumentRuntimeId,
        EditorNodeFingerprint[] Ancestors,
        Rect Bounds);

    private sealed record EditorBinding(
        AutomationElement Element,
        TextPattern? TextPattern,
        ValuePattern? ValuePattern,
        string? RuntimeId,
        EditorFingerprint Fingerprint,
        AutomationElement? DocumentRoot,
        long Generation);

    private sealed record EditorCandidate(
        AutomationElement Element,
        TextPattern? TextPattern,
        ValuePattern? ValuePattern,
        EditorFingerprint Fingerprint,
        int Score,
        bool IsFocused);

    private sealed record EditorAccess(
        AutomationElement Element,
        TextPattern? TextPattern,
        ValuePattern? ValuePattern,
        long Generation,
        string? RuntimeId,
        string? DocumentRuntimeId);

    private sealed record AutomationNotice(
        AutomationNoticeKind Kind,
        long Generation,
        AutomationElement? Element = null,
        int X = 0,
        int Y = 0,
        bool TargetForegroundMatched = false,
        SubmitCandidate? PreparedCandidate = null);

    private enum AutomationNoticeKind
    {
        EditorChanged,
        FocusChanged,
        Invoked,
        MouseButtonDown
    }

    private enum EditorTrackingState
    {
        Starting,
        Tracking,
        Reacquiring,
        Suspended,
        Submitted
    }

    private void UpdateTrackedEditorFocus(AutomationElement? editor)
    {
        long focusedAt = 0;
        if (editor is not null && _trackedEditorIdentity is not null)
        {
            try
            {
                var focusedEditor = FindFocusedEditor(_targetWindow, out _, out _);
                if (focusedEditor is not null && IsSameTrackedEditor(focusedEditor))
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

    private static string GetProcessName(int processId)
    {
        if (processId <= 0) return "unknown";
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
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
                if (TryGetEditableTextPatterns(element, out textPattern, out valuePattern))
                {
                    return element;
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

    private static bool IsWithinElement(AutomationElement element, AutomationElement ancestor)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < 64; depth++)
        {
            if (AreSameAutomationElement(element, ancestor)) return true;
            try { element = walker.GetParent(element); }
            catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { return false; }
        }
        return false;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or
            UnauthorizedAccessException or ArgumentException;

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
        string ReadState,
        string? EditorRuntimeId = null,
        string? DocumentRuntimeId = null,
        long EditorGeneration = 0)
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
        bool UsedRecentPreClearSnapshot,
        int OutgoingProofAttempts = 0);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public NativePoint Point;
        public uint MouseDataValue;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
