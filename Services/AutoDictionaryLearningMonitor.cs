using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterFlow.Services;

internal sealed record DictionaryCorrectionSubmission(string OriginalText, string CorrectedText);

internal sealed class AutoDictionaryLearningMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint LlkhfInjected = 0x10;
    private const int VkReturn = 0x0D;
    private const int VkShift = 0x10;
    private const int MaxDocumentLength = 24_000;
    private const int MaxMonitorMinutes = 10;

    private readonly IntPtr _targetWindow;
    private readonly string _pastedText;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _submitSignal = new(0, 1);
    private readonly object _snapshotLock = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private IntPtr _keyboardHook;
    private Task? _monitorTask;
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private string _originalSegment = string.Empty;
    private string _lastSegment = string.Empty;
    private string _lastFullText = string.Empty;
    private string _submitSegment = string.Empty;
    private string _submitFullText = string.Empty;
    private bool _snapshotInvalid;
    private bool _submitSnapshotValid;
    private int _initialized;
    private int _disposed;

    private AutoDictionaryLearningMonitor(IntPtr targetWindow, string pastedText)
    {
        _targetWindow = targetWindow;
        _pastedText = NormalizeLineEndings(pastedText);
        _token = _cancellation.Token;
        _keyboardProc = KeyboardHookCallback;
    }

    public event EventHandler<DictionaryCorrectionSubmission>? SubmissionConfirmed;

    public static AutoDictionaryLearningMonitor? TryStart(IntPtr targetWindow, string pastedText)
    {
        if (targetWindow == IntPtr.Zero || string.IsNullOrWhiteSpace(pastedText) ||
            pastedText.Length > MaxDocumentLength || PasteService.GetForegroundWindowHandle() != targetWindow)
            return null;

        var monitor = new AutoDictionaryLearningMonitor(targetWindow, pastedText);
        var module = GetModuleHandle(null);
        monitor._keyboardHook = SetWindowsHookEx(WhKeyboardLl, monitor._keyboardProc, module, 0);
        if (monitor._keyboardHook == IntPtr.Zero)
        {
            monitor.Dispose();
            return null;
        }

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
            _lastSegment = string.Empty;
            _lastFullText = string.Empty;
            _submitSegment = string.Empty;
            _submitFullText = string.Empty;
        }
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
        var token = _token;
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

            if (editor is null || !TryReadText(editor, textPattern, valuePattern, out var initialText)) return;
            initialText = NormalizeLineEndings(initialText);
            if (initialText.Length > MaxDocumentLength) return;
            var insertedStart = initialText.IndexOf(_pastedText, StringComparison.Ordinal);
            if (insertedStart < 0 || initialText.IndexOf(_pastedText, insertedStart + _pastedText.Length,
                    StringComparison.Ordinal) >= 0)
                return;

            lock (_snapshotLock)
            {
                _prefix = initialText[..insertedStart];
                _suffix = initialText[(insertedStart + _pastedText.Length)..];
                _originalSegment = _pastedText;
                _lastSegment = _pastedText;
                _lastFullText = initialText;
            }
            Volatile.Write(ref _initialized, 1);

            textChangedHandler = (_, _) => RefreshSnapshot(editor, textPattern, valuePattern);
            try { Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, editor,
                TreeScope.Element, textChangedHandler); }
            catch (Exception ex) when (IsAutomationFailure(ex)) { textChangedHandler = null; }
            if (valuePattern is not null)
            {
                valueChangedHandler = (_, _) => RefreshSnapshot(editor, textPattern, valuePattern);
                try { Automation.AddAutomationPropertyChangedEventHandler(editor, TreeScope.Element,
                    valueChangedHandler, ValuePattern.ValueProperty); }
                catch (Exception ex) when (IsAutomationFailure(ex)) { valueChangedHandler = null; }
            }

            try
            {
                root = AutomationElement.FromHandle(_targetWindow);
                invokedHandler = OnAutomationEvent;
                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, root,
                    TreeScope.Descendants, invokedHandler);
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { }

            while (!monitorToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), monitorToken).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0) return;

                var submitDetected = _submitSignal.Wait(0);
                if (TryReadText(editor, textPattern, valuePattern, out var currentText))
                {
                    currentText = NormalizeLineEndings(currentText);
                    if (currentText.Length > MaxDocumentLength) return;
                    if (TryExtractSegment(currentText, out var segment))
                    {
                        SetSnapshot(currentText, segment);
                    }
                    else if (!submitDetected)
                    {
                        lock (_snapshotLock) _snapshotInvalid = true;
                        return;
                    }
                }

                if (!submitDetected) continue;
                string submittedCorrection;
                string textBeforeSubmit;
                string originalSegment;
                lock (_snapshotLock)
                {
                    if (!_submitSnapshotValid) return;
                    submittedCorrection = _submitSegment;
                    textBeforeSubmit = _submitFullText;
                    originalSegment = _originalSegment;
                }
                if (string.Equals(submittedCorrection, originalSegment, StringComparison.Ordinal))
                {
                    // The user sent the original voice text without correcting it.
                    return;
                }

                for (var confirmAttempt = 0; confirmAttempt < 8; confirmAttempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(180), monitorToken).ConfigureAwait(false);
                    if (!TryReadText(editor, textPattern, valuePattern, out var afterSubmit)) continue;
                    afterSubmit = NormalizeLineEndings(afterSubmit);
                    if (afterSubmit.Length > MaxDocumentLength) return;
                    var editorChanged = !string.Equals(afterSubmit, textBeforeSubmit, StringComparison.Ordinal);
                    var contentCleared = afterSubmit.Length == 0 ||
                                         !TryExtractSegment(afterSubmit, out var remainingSegment) ||
                                         remainingSegment.Length == 0;
                    if (!editorChanged || !contentCleared) continue;

                    SubmissionConfirmed?.Invoke(this,
                        new DictionaryCorrectionSubmission(originalSegment, submittedCorrection));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
        finally
        {
            if (root is not null && invokedHandler is not null)
            {
                try { Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, root, invokedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (editor is not null && textChangedHandler is not null)
            {
                try { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, editor, textChangedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
            if (editor is not null && valueChangedHandler is not null)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(editor, valueChangedHandler); }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
            }
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
            if (current.ControlType != ControlType.Button) return;
            var name = current.Name ?? string.Empty;
            if (name.Contains("送信", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("send", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("投稿", StringComparison.OrdinalIgnoreCase))
                SignalSubmit();
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Volatile.Read(ref _initialized) != 0 && Volatile.Read(ref _disposed) == 0 &&
            wParam.ToInt32() is WmKeyDown or WmSysKeyDown)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (key.VirtualKey == VkReturn && (key.Flags & LlkhfInjected) == 0 &&
                (GetAsyncKeyState(VkShift) & 0x8000) == 0 &&
                GetForegroundWindow() == _targetWindow)
                SignalSubmit();
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void SignalSubmit()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            lock (_snapshotLock)
            {
                _submitSnapshotValid = !_snapshotInvalid;
                _submitSegment = _lastSegment;
                _submitFullText = _lastFullText;
            }
            if (_submitSignal.CurrentCount == 0) _submitSignal.Release();
        }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
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
        if (Volatile.Read(ref _disposed) != 0 ||
            !TryReadText(editor, textPattern, valuePattern, out var currentText))
            return;
        currentText = NormalizeLineEndings(currentText);
        if (currentText.Length > MaxDocumentLength) return;
        if (!TryExtractSegment(currentText, out var segment))
        {
            lock (_snapshotLock) _snapshotInvalid = true;
            return;
        }
        SetSnapshot(currentText, segment);
    }

    private void SetSnapshot(string fullText, string segment)
    {
        lock (_snapshotLock)
        {
            _lastFullText = fullText;
            _lastSegment = segment;
            _snapshotInvalid = false;
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
        out string text)
    {
        text = string.Empty;
        try
        {
            if (element.Current.IsPassword) return false;
            if (valuePattern is not null)
                text = valuePattern.Current.Value ?? string.Empty;
            else if (textPattern is not null)
                text = textPattern.DocumentRange.GetText(MaxDocumentLength + 1);
            else
                return false;
            return true;
        }
        catch (Exception ex) when (IsAutomationFailure(ex) || ex is ObjectDisposedException) { return false; }
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
