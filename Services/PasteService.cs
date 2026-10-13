using System.Runtime.InteropServices;

namespace BetterFlow.Services;

internal static class PasteService
{
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

internal enum PasteResult { Pasted, CopiedOnly }
