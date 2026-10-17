using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterFlow.Services;

/// <summary>Samples a small frosted backdrop once before a transparent overlay is shown.</summary>
internal sealed class GlassBackdropSampler : IDisposable
{
    private const int PaddingDip = 12;
    private readonly HwndSource _source;
    private readonly System.Windows.Controls.Image _image;
    private bool _disposed;
    internal bool IsEnabled { get; private set; }

    internal GlassBackdropSampler(HwndSource source, System.Windows.Controls.Image image)
    {
        _source = source;
        _image = image;
    }

    internal void PrepareBackground()
    {
        if (_disposed || _source.IsDisposed) return;
        // A visible indicator must never become its own background. Live desktop colors still
        // pass through the translucent image and tint; no capture exclusion is used.
        if (IsWindowVisible(_source.Handle)) return;
        _image.Visibility = Visibility.Collapsed;
        _image.Source = null;
        IsEnabled = false;
        if (SystemParameters.HighContrast || !GetWindowRect(_source.Handle, out var rect)) return;
        var dpi = VisualTreeHelper.GetDpi(_image);
        var paddingX = (int)Math.Ceiling(PaddingDip * dpi.DpiScaleX);
        var paddingY = (int)Math.Ceiling(PaddingDip * dpi.DpiScaleY);
        var width = rect.Right - rect.Left + paddingX * 2;
        var height = rect.Bottom - rect.Top + paddingY * 2;
        if (width <= 0 || height <= 0 || width > 2048 || height > 1024) return;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;
        IntPtr memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, originalBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return;
            var info = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32
            };
            bitmap = CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return;
            originalBitmap = SelectObject(memoryDc, bitmap);
            if (originalBitmap == IntPtr.Zero || originalBitmap == new IntPtr(-1)) return;
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, rect.Left - paddingX,
                    rect.Top - paddingY, 0x40CC0020)) return;
            // Only this small padded surface exists in memory. It is never saved or sent to AI.
            var frame = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
            frame.WritePixels(new Int32Rect(0, 0, width, height), bits, width * height * 4, width * 4);
            frame.Freeze();
            _image.Source = frame;
            _image.Visibility = Visibility.Visible;
            IsEnabled = true;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && originalBitmap != IntPtr.Zero && originalBitmap != new IntPtr(-1))
                SelectObject(memoryDc, originalBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearBackground();
    }

    internal void ClearBackground()
    {
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        IsEnabled = false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
