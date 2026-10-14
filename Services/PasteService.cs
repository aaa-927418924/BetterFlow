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
        System.Windows.Clipboard.SetText(text);
        if (targetWindow != IntPtr.Zero && GetForegroundWindow() == targetWindow)
        {
            var inputs = new[]
            {
                CreateKeyInput(VirtualKeyControl),
                CreateKeyInput(VirtualKeyV),
                CreateKeyInput(VirtualKeyV, keyUp: true),
                CreateKeyInput(VirtualKeyControl, keyUp: true)
            };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == (uint)inputs.Length
                ? PasteResult.Pasted
                : PasteResult.CopiedOnly;
        }
        return PasteResult.CopiedOnly;
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
