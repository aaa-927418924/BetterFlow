using System.Runtime.InteropServices;

namespace BetterFlow.Services;

internal static class FullscreenWindowDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const int FullscreenEdgeTolerance = 2;

    public static bool IsForegroundFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window) || IsZoomed(window))
            return false;

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out var visibleBounds,
                (uint)Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(window, out visibleBounds))
            return false;

        return MatchesMonitorBounds(visibleBounds, monitorInfo.Monitor);
    }

    private static bool MatchesMonitorBounds(NativeRect window, NativeRect monitor) =>
        Math.Abs((long)window.Left - monitor.Left) <= FullscreenEdgeTolerance &&
        Math.Abs((long)window.Top - monitor.Top) <= FullscreenEdgeTolerance &&
        Math.Abs((long)window.Right - monitor.Right) <= FullscreenEdgeTolerance &&
        Math.Abs((long)window.Bottom - monitor.Bottom) <= FullscreenEdgeTolerance;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out NativeRect value,
        uint valueSize);
}
