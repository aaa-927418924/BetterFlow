using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace BetterFlow.Services;

internal static class PasteService
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;
    private const ushort VirtualKeyReturn = 0x0D;
    private const int MaximumSubmitButtons = 2000;
    private const int MaximumFocusAncestors = 24;

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public static PasteResult CopyAndPaste(string text, IntPtr targetWindow)
    {
        CopyTextToClipboard(text);
        return PasteCopiedText(targetWindow);
    }

    public static void CopyTextToClipboard(string text) => System.Windows.Clipboard.SetText(text);

    public static PasteResult PasteCopiedText(IntPtr targetWindow)
        => PasteCopiedTextWithDetails(targetWindow).Result;

    public static PasteAttemptResult PasteCopiedTextWithDetails(IntPtr targetWindow)
    {
        if (targetWindow != IntPtr.Zero && GetForegroundWindow() == targetWindow)
        {
            var inputs = new[]
            {
                CreateKeyInput(VirtualKeyControl),
                CreateKeyInput(VirtualKeyV),
                CreateKeyInput(VirtualKeyV, keyUp: true),
                CreateKeyInput(VirtualKeyControl, keyUp: true)
            };
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
            var lastError = sent < (uint)inputs.Length ? Marshal.GetLastWin32Error() : (int?)null;
            if (sent < (uint)inputs.Length)
            {
                // A partial Ctrl+V sequence can leave a modifier logically held.
                // Release both keys before returning the failed attempt.
                var releases = new[]
                {
                    CreateKeyInput(VirtualKeyV, keyUp: true),
                    CreateKeyInput(VirtualKeyControl, keyUp: true)
                };
                SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
            }

            return new PasteAttemptResult(
                sent == inputs.Length ? PasteResult.Pasted : PasteResult.CopiedOnly,
                sent,
                (uint)inputs.Length,
                lastError);
        }
        return new PasteAttemptResult(PasteResult.CopiedOnly, 0, 4, null);
    }

    public static bool SendEnterKey(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || GetForegroundWindow() != targetWindow)
            return false;

        var inputs = new[]
        {
            CreateKeyInput(VirtualKeyReturn),
            CreateKeyInput(VirtualKeyReturn, keyUp: true)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent < (uint)inputs.Length)
            SendInput(1, [CreateKeyInput(VirtualKeyReturn, keyUp: true)], Marshal.SizeOf<NativeInput>());
        return sent == (uint)inputs.Length;
    }

    public static bool TryInvokeSendButton(IntPtr targetWindow)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(targetWindow); }
        catch { return false; }

        AutomationElement? focused;
        try
        {
            focused = AutomationElement.FocusedElement;
            if (focused is null || !IsWithinWindow(focused, targetWindow))
                focused = null;
        }
        catch { focused = null; }

        var candidateCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true));
        AutomationElementCollection candidates;
        try { candidates = root.FindAll(TreeScope.Descendants, candidateCondition); }
        catch { return false; }

        var rootBounds = SafeBounds(root);
        var focusBounds = FindFocusedEditorBounds(focused);
        var namedCandidates = new List<(AutomationElement Element, double Score)>();
        var limit = Math.Min(candidates.Count, MaximumSubmitButtons);
        for (var index = 0; index < limit; index++)
        {
            var element = candidates[index];
            try
            {
                var current = element.Current;
                if (!current.IsEnabled || current.IsOffscreen || !HasSendLabel(current))
                    continue;
                if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                    continue;

                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                    continue;
                namedCandidates.Add((element, GetCandidateScore(bounds, focusBounds, rootBounds)));
            }
            catch { }
        }

        foreach (var candidate in namedCandidates.OrderBy(candidate => candidate.Score))
        {
            if (TryInvoke(candidate.Element, targetWindow))
                return true;
        }

        return focused is not null && TryInvokeComposerActionButton(
            focused, targetWindow, rootBounds, candidateCondition);
    }

    private static bool TryInvokeComposerActionButton(
        AutomationElement focused,
        IntPtr targetWindow,
        Rect rootBounds,
        System.Windows.Automation.Condition candidateCondition)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? ancestor = focused;
        for (var depth = 0; ancestor is not null && depth < MaximumFocusAncestors; depth++)
        {
            try
            {
                var current = ancestor.Current;
                if (current.ControlType == ControlType.Group ||
                    current.ControlType == ControlType.Custom ||
                    current.ControlType == ControlType.Pane)
                {
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && bounds.Width >= 120 && bounds.Height >= 24 &&
                        bounds.Width < rootBounds.Width * 0.95 && bounds.Height < rootBounds.Height * 0.7)
                    {
                        var candidates = ancestor.FindAll(TreeScope.Descendants, candidateCondition);
                        var buttons = new List<(AutomationElement Element, Rect Bounds)>();
                        for (var index = 0; index < candidates.Count && index < 128; index++)
                        {
                            var element = candidates[index];
                            try
                            {
                                var button = element.Current;
                                if (!button.IsEnabled || button.IsOffscreen ||
                                    button.ControlType != ControlType.Button &&
                                    !element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                                    continue;

                                var buttonBounds = button.BoundingRectangle;
                                if (buttonBounds.IsEmpty || !bounds.Contains(buttonBounds.TopLeft) ||
                                    !bounds.Contains(buttonBounds.BottomRight))
                                    continue;
                                buttons.Add((element, buttonBounds));
                            }
                            catch { }
                        }

                        if (buttons.Count > 0)
                        {
                            var rightmostEdge = buttons.Max(button => button.Bounds.Right);
                            var structuralCandidates = buttons
                                .Where(button =>
                                    button.Bounds.Left + button.Bounds.Width / 2 >= bounds.Left + bounds.Width * 0.62 &&
                                    button.Bounds.Top + button.Bounds.Height / 2 >= bounds.Top + bounds.Height * 0.35 &&
                                    button.Bounds.Right >= rightmostEdge - Math.Max(36, button.Bounds.Width * 0.8))
                                .OrderByDescending(button => button.Bounds.Right)
                                .ThenByDescending(button => button.Bounds.Bottom);

                            foreach (var candidate in structuralCandidates)
                            {
                                if (TryInvoke(candidate.Element, targetWindow))
                                    return true;
                            }
                        }
                    }
                }
            }
            catch { }

            try { ancestor = walker.GetParent(ancestor); }
            catch { ancestor = null; }
        }

        return false;
    }

    private static bool TryInvoke(AutomationElement element, IntPtr targetWindow)
    {
        if (GetForegroundWindow() != targetWindow)
            return false;

        try
        {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ||
                pattern is not InvokePattern invoke)
                return false;
            invoke.Invoke();
            return true;
        }
        catch { return false; }
    }

    private static bool HasSendLabel(AutomationElement.AutomationElementInformation current)
    {
        var label = $"{current.Name} {current.AutomationId} {current.HelpText}";
        return label.Contains("送信", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("送る", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("投稿", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("send", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("submit", StringComparison.OrdinalIgnoreCase);
    }

    private static Rect FindFocusedEditorBounds(AutomationElement? focused)
    {
        if (focused is null) return Rect.Empty;
        var walker = TreeWalker.ControlViewWalker;
        var current = focused;
        for (var depth = 0; current is not null && depth < MaximumFocusAncestors; depth++)
        {
            try
            {
                var info = current.Current;
                if (info.ControlType == ControlType.Edit || info.ControlType == ControlType.Document ||
                    current.TryGetCurrentPattern(TextPattern.Pattern, out _) ||
                    current.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                {
                    var bounds = info.BoundingRectangle;
                    if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                        return bounds;
                }
            }
            catch { }

            try { current = walker.GetParent(current); }
            catch { current = null; }
        }
        return Rect.Empty;
    }

    private static bool IsWithinWindow(AutomationElement element, IntPtr targetWindow)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? current = element;
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            try
            {
                var handle = current.Current.NativeWindowHandle;
                if (handle != 0 && new IntPtr(handle) == targetWindow)
                    return true;
            }
            catch { return false; }

            try { current = walker.GetParent(current); }
            catch { return false; }
        }
        return false;
    }

    private static Rect SafeBounds(AutomationElement element)
    {
        try { return element.Current.BoundingRectangle; }
        catch { return Rect.Empty; }
    }

    private static double GetCandidateScore(Rect candidate, Rect focused, Rect root)
    {
        var focusDistance = focused.IsEmpty ? 0 : GetRectDistanceSquared(candidate, focused);
        var bottomDistance = root.IsEmpty ? 0 : Math.Pow(Math.Max(0, root.Bottom - candidate.Bottom), 2) * 0.2;
        var rightDistance = root.IsEmpty ? 0 : Math.Pow(Math.Max(0, root.Right - candidate.Right), 2) * 0.1;
        return focusDistance + bottomDistance + rightDistance;
    }

    private static double GetRectDistanceSquared(Rect first, Rect second)
    {
        var x = Math.Max(0, Math.Max(first.Left - second.Right, second.Left - first.Right));
        var y = Math.Max(0, Math.Max(first.Top - second.Bottom, second.Top - first.Bottom));
        return x * x + y * y;
    }

    private static NativeInput CreateKeyInput(ushort virtualKey, bool keyUp = false) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeKeyboardInput
        {
            VirtualKey = virtualKey,
            Flags = keyUp ? KeyUp : 0
        }
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

internal enum PasteResult { Pasted, CopiedOnly }

internal readonly record struct PasteAttemptResult(
    PasteResult Result,
    uint EventsSent,
    uint EventsRequested,
    int? LastErrorCode);
