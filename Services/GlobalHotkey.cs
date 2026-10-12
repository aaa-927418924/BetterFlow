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
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;
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
    private const int VkMenu = 0x12;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;

    private IntPtr _handle;
    private IntPtr _keyboardHook;
    private HotkeyBinding _binding;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _registeredAsHotkey;
    private bool _registered;
    private bool _rightAltPressed;
    private bool _leftAltDown;
    private bool _leftControlDown;
    private bool _rightControlDown;
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;

    public bool Register(IntPtr handle, HotkeyBinding binding)
    {
        Unregister();
        _handle = handle;
        _binding = binding;
        if (handle == IntPtr.Zero || !binding.IsUsable)
            return false;

        if (binding.IsStandaloneRightAlt)
        {
            _leftAltDown = IsKeyDown(VkLMenu);
            _leftControlDown = IsKeyDown(VkLControl);
            _rightControlDown = IsKeyDown(VkRControl);
            _leftShiftDown = IsKeyDown(VkLShift);
            _rightShiftDown = IsKeyDown(VkRShift);
            _leftWindowsDown = IsKeyDown(VkLWin);
            _rightWindowsDown = IsKeyDown(VkRWin);
            _keyboardProc = KeyboardHookCallback;
            var module = GetModuleHandle(null);
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
        _leftAltDown = false;
        _leftControlDown = false;
        _rightControlDown = false;
        _leftShiftDown = false;
        _rightShiftDown = false;
        _leftWindowsDown = false;
        _rightWindowsDown = false;
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
            var isKeyDown = message is WmKeyDown or WmSysKeyDown;
            var isKeyUp = message is WmKeyUp or WmSysKeyUp;
            if (IsRightAltKey(keyInfo))
            {
                if (isKeyDown && !IsOtherModifierDown())
                {
                    if (!_rightAltPressed)
                    {
                        _rightAltPressed = true;
                        PostMessage(_handle, MessageId, new IntPtr(HotkeyId), IntPtr.Zero);
                    }
                    return new IntPtr(1);
                }

                if (isKeyUp && _rightAltPressed)
                {
                    _rightAltPressed = false;
                    return new IntPtr(1);
                }
            }

            UpdateModifierState(keyInfo, isKeyDown, isKeyUp);
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool IsOtherModifierDown() =>
        _leftAltDown || _leftControlDown || _rightControlDown
        || _leftShiftDown || _rightShiftDown || _leftWindowsDown || _rightWindowsDown;

    private void UpdateModifierState(KeyboardData keyInfo, bool isKeyDown, bool isKeyUp)
    {
        if ((!isKeyDown && !isKeyUp) || (keyInfo.Flags & LlkhfInjected) != 0)
            return;

        var key = (int)keyInfo.VirtualKey;
        if (key == VkMenu)
            key = (keyInfo.Flags & LlkhfExtended) != 0 ? (int)HotkeyBinding.RightAltVirtualKey : VkLMenu;
        else if (key == VkControl)
            key = (keyInfo.Flags & LlkhfExtended) != 0 ? VkRControl : VkLControl;
        else if (key == VkShift)
            key = keyInfo.ScanCode == 0x36 ? VkRShift : VkLShift;

        var down = isKeyDown;
        switch (key)
        {
            case VkLMenu: _leftAltDown = down; break;
            case VkLControl: _leftControlDown = down; break;
            case VkRControl: _rightControlDown = down; break;
            case VkLShift: _leftShiftDown = down; break;
            case VkRShift: _rightShiftDown = down; break;
            case VkLWin: _leftWindowsDown = down; break;
            case VkRWin: _rightWindowsDown = down; break;
        }
    }

    private static bool IsRightAltKey(KeyboardData keyInfo) =>
        keyInfo.VirtualKey == HotkeyBinding.RightAltVirtualKey
        || (keyInfo.VirtualKey == VkMenu && (keyInfo.Flags & LlkhfExtended) != 0);

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
