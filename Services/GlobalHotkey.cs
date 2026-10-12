using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AutoPrompt.Services;

internal sealed class GlobalHotkey : IDisposable
{
    public const int MessageId = 0x0312;
    private const int HotkeyId = 0x4150;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;

    private IntPtr _handle;

    public bool Register(IntPtr handle)
    {
        _handle = handle;
        return RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkSpace);
    }

    public bool IsMessage(int id) => id == HotkeyId;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
