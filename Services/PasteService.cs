using System.Runtime.InteropServices;
using System.Globalization;

namespace BetterFlow.Services;

internal static class PasteService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyLeft = 0x25;

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public static PasteResult CopyAndPaste(string text, IntPtr targetWindow)
    {
        System.Windows.Clipboard.SetText(text);
        if (targetWindow != IntPtr.Zero && GetForegroundWindow() == targetWindow)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            return PasteResult.Pasted;
        }
        return PasteResult.CopiedOnly;
    }

    public static bool SelectTextBeforeCaret(string text, IntPtr targetWindow)
    {
        if (string.IsNullOrEmpty(text) || GetForegroundWindow() != targetWindow)
            return false;

        var textElementCount = StringInfo.ParseCombiningCharacters(text).Length;
        if (textElementCount == 0)
            return false;

        var inputs = new Input[textElementCount * 2 + 2];
        inputs[0] = CreateKeyboardInput(VirtualKeyShift, keyUp: false);
        for (var index = 0; index < textElementCount; index++)
        {
            inputs[index * 2 + 1] = CreateKeyboardInput(VirtualKeyLeft, keyUp: false);
            inputs[index * 2 + 2] = CreateKeyboardInput(VirtualKeyLeft, keyUp: true);
        }
        inputs[^1] = CreateKeyboardInput(VirtualKeyShift, keyUp: true);

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
            return true;

        // A partial SendInput batch can leave Shift held. Release it before returning.
        keybd_event((byte)VirtualKeyShift, 0, KeyEventKeyUp, UIntPtr.Zero);
        return false;
    }

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

internal enum PasteResult { Pasted, CopiedOnly }
