using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterFlow.Services;

internal sealed class DiscordPushToMuteController : IDisposable
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;
    private const ushort VkLeftControl = 0xA2;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkLeftAlt = 0xA4;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkRightAlt = 0xA5;
    private const ushort VkRightControl = 0xA3;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;

    private readonly object _sync = new();
    private readonly List<ushort> _pressedKeys = new();
    private int _leaseCount;
    private HotkeyBinding _activeBinding;
    private bool _disposed;

    public IDisposable Acquire(bool enabled, HotkeyBinding binding)
    {
        if (!enabled || !IsDiscordDesktopRunning())
            return EmptyLease.Instance;

        if (!IsSupportedBinding(binding))
            throw new InvalidOperationException("Discord自動ミュートを使うには、先にショートカットキーを設定してください。");

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leaseCount == 0)
            {
                if (!PressBinding(binding))
                    throw new InvalidOperationException(
                        "Discord用ショートカットを送信できませんでした。DiscordとBetter Flowを同じ権限で起動し、Discord側にも同じキーを設定してください。");
                _activeBinding = binding;
            }
            else if (_activeBinding != binding)
            {
                throw new InvalidOperationException("音声入力中にDiscordミュートのキー設定が変更されました。録音を停止して再試行してください。");
            }

            _leaseCount++;
            return new Lease(this);
        }
    }

    public static bool IsSupportedBinding(HotkeyBinding binding) =>
        binding.VirtualKey is > 0 and <= 0xFF &&
        (binding.Modifiers & ~0x0Fu) == 0 &&
        (binding.Modifiers != 0 || binding.VirtualKey == HotkeyBinding.RightAltVirtualKey ||
         binding.VirtualKey is >= 0x70 and <= 0x87);

    private static bool IsDiscordDesktopRunning()
    {
        foreach (var processName in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }

            var foundRunningProcess = false;
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited) foundRunningProcess = true;
                    }
                    catch { }
                }
            }
            if (foundRunningProcess) return true;
        }

        return false;
    }

    private bool PressBinding(HotkeyBinding binding)
    {
        var keys = new List<ushort>(5);
        if ((binding.Modifiers & ModControl) != 0) keys.Add(VkLeftControl);
        if ((binding.Modifiers & ModShift) != 0) keys.Add(VkLeftShift);
        if ((binding.Modifiers & ModAlt) != 0) keys.Add(VkLeftAlt);
        if ((binding.Modifiers & ModWindows) != 0) keys.Add(VkLeftWindows);
        keys.Add((ushort)binding.VirtualKey);

        var inputs = keys.Select(key => CreateKeyboardInput(key, keyUp: false)).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            for (var index = Math.Min((int)sent, keys.Count) - 1; index >= 0; index--)
                TrySendOneKeyboardInput(keys[index], keyUp: true);
            return false;
        }

        _pressedKeys.AddRange(keys);
        return true;
    }

    private void ReleaseBinding()
    {
        for (var index = _pressedKeys.Count - 1; index >= 0; index--)
        {
            var key = _pressedKeys[index];
            if (TrySendOneKeyboardInput(key, keyUp: true)) continue;

            // A transient input-blocking thread can reject one event. Retry once
            // before proceeding so the application does not leave a key held.
            TrySendOneKeyboardInput(key, keyUp: true);
        }

        _pressedKeys.Clear();
        _activeBinding = default;
    }

    private void ReleaseLease()
    {
        lock (_sync)
        {
            if (_leaseCount == 0) return;
            _leaseCount--;
            if (_leaseCount == 0) ReleaseBinding();
        }
    }

    private static bool TrySendOneKeyboardInput(ushort key, bool keyUp) =>
        SendInput(1, [CreateKeyboardInput(key, keyUp)], Marshal.SizeOf<NativeInput>()) == 1;

    private static NativeInput CreateKeyboardInput(ushort key, bool keyUp)
    {
        var flags = IsExtendedKey(key) ? KeyeventfExtendedKey : 0;
        if (keyUp) flags |= KeyeventfKeyUp;
        return new NativeInput
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = flags
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort key) => key is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or
        VkRightControl or VkRightAlt;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _leaseCount = 0;
            ReleaseBinding();
        }
    }

    private sealed class Lease(DiscordPushToMuteController owner) : IDisposable
    {
        private DiscordPushToMuteController? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
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
        public int Dx;
        public int Dy;
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
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
