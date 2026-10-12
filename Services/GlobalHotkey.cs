using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;

namespace AutoPrompt.Services;

internal readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public const uint RightAltVirtualKey = 0xA5;
    public bool IsStandaloneRightAlt => Modifiers == 0 && VirtualKey == RightAltVirtualKey;

    public bool IsUsable => VirtualKey is > 0 and <= 0xFF
                            && (Modifiers & ~0x0Fu) == 0
                            && (Modifiers != 0 || IsStandaloneRightAlt);
}

internal sealed class GlobalHotkey : IDisposable
{
    public const int MessageId = 0x0312;
    private const int HotkeyId = 0x4150;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int VkLMenu = 0xA4;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private IntPtr _handle;
    private IntPtr _keyboardHook;
    private HotkeyBinding _binding;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _registeredAsHotkey;
    private bool _registered;
    private bool _rightAltPressed;

    public bool Register(IntPtr handle, HotkeyBinding binding)
    {
        Unregister();
        _handle = handle;
        _binding = binding;
        if (handle == IntPtr.Zero || !binding.IsUsable)
            return false;

        if (binding.IsStandaloneRightAlt)
        {
            _registeredAsHotkey = RegisterHotKey(handle, HotkeyId, ModNoRepeat, binding.VirtualKey);
            if (_registeredAsHotkey)
            {
                _registered = true;
                return true;
            }

            _keyboardProc = KeyboardHookCallback;
            using var process = Process.GetCurrentProcess();
            var moduleName = process.MainModule?.ModuleName;
            var module = GetModuleHandle(moduleName);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            _registered = _keyboardHook != IntPtr.Zero;
            return _registered;
        }

        _registeredAsHotkey = RegisterHotKey(handle, HotkeyId, binding.Modifiers | ModNoRepeat, binding.VirtualKey);
        _registered = _registeredAsHotkey;
        return _registered;
    }

    public void Unregister()
    {
        if (_registeredAsHotkey && _handle != IntPtr.Zero)
            UnregisterHotKey(_handle, HotkeyId);
        _registeredAsHotkey = false;

        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _keyboardProc = null;
        _registered = false;
        _rightAltPressed = false;
    }

    public bool IsMessage(int id) => id == HotkeyId;

    public static string Format(HotkeyBinding binding)
    {
        var parts = new List<string>(5);
        if ((binding.Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((binding.Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((binding.Modifiers & ModWindows) != 0) parts.Add("Windows");

        var key = KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey);
        parts.Add(key switch
        {
            System.Windows.Input.Key.RightAlt => "右Alt",
            System.Windows.Input.Key.LeftAlt => "左Alt",
            System.Windows.Input.Key.Space => "Space",
            System.Windows.Input.Key.Return => "Enter",
            System.Windows.Input.Key.Escape => "Esc",
            System.Windows.Input.Key.Back => "Backspace",
            System.Windows.Input.Key.Delete => "Delete",
            System.Windows.Input.Key.Tab => "Tab",
            _ when key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9 => key.ToString()[1..],
            _ => key.ToString()
        });
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        Unregister();
        _handle = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _binding.IsStandaloneRightAlt)
        {
            var message = wParam.ToInt32();
            var keyInfo = Marshal.PtrToStructure<KeyboardData>(lParam);
            if (keyInfo.VirtualKey == HotkeyBinding.RightAltVirtualKey)
            {
                if ((message is WmKeyDown or WmSysKeyDown) && !IsOtherModifierDown())
                {
                    _rightAltPressed = true;
                    return new IntPtr(1);
                }

                if ((message is WmKeyUp or WmSysKeyUp) && _rightAltPressed)
                {
                    var shouldTrigger = !IsOtherModifierDown();
                    _rightAltPressed = false;
                    if (shouldTrigger)
                        PostMessage(_handle, MessageId, new IntPtr(HotkeyId), IntPtr.Zero);
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool IsOtherModifierDown() =>
        IsKeyDown(VkLMenu) || IsKeyDown(VkLControl) || IsKeyDown(VkRControl)
        || IsKeyDown(VkLShift) || IsKeyDown(VkRShift) || IsKeyDown(VkLWin) || IsKeyDown(VkRWin);

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

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
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
