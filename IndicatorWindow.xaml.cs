using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BetterFlow;

public partial class IndicatorWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private readonly DispatcherTimer _hideTimer;

    public IndicatorWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        SourceInitialized += (_, _) => MakeClickThrough();
        Closed += (_, _) => _hideTimer.Stop();
    }

    public void ShowStatus(string text, TimeSpan? autoHide = null)
    {
        _hideTimer.Stop();
        StatusText.Text = text;
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - Height - 12);

        if (!IsVisible)
            Show();

        if (autoHide is { } duration)
        {
            _hideTimer.Interval = duration;
            _hideTimer.Start();
        }
    }

    public void HideStatus()
    {
        _hideTimer.Stop();
        Hide();
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(currentStyle | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
