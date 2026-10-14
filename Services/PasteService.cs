using System.Runtime.InteropServices;

namespace BetterFlow.Services;

internal static class PasteService
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;

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
