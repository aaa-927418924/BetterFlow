using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BetterFlow;

public partial class IndicatorWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const double IconCanvasHeight = 24.4;
    private const double LeftBarIdleHeight = IconCanvasHeight * 41 / 77;
    private const double CenterBarIdleHeight = IconCanvasHeight * 46 / 77;
    private const double RightBarIdleHeight = IconCanvasHeight * 29 / 77;
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

    public void SetAudioLevel(float? normalizedLevel)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => SetAudioLevel(normalizedLevel)));
            return;
        }

        if (normalizedLevel is null)
        {
            LeftWaveBar.Height = LeftBarIdleHeight;
            CenterWaveBar.Height = CenterBarIdleHeight;
            RightWaveBar.Height = RightBarIdleHeight;
            CenterBarsVertically();
            return;
        }

        var level = Math.Clamp(normalizedLevel.Value, 0f, 1f);
        LeftWaveBar.Height = 4.5 + (LeftBarIdleHeight - 4.5) * level;
        CenterWaveBar.Height = 5.5 + (CenterBarIdleHeight - 5.5) * level;
        RightWaveBar.Height = 4.0 + (RightBarIdleHeight - 4.0) * level;
        CenterBarsVertically();
    }

    private void CenterBarsVertically()
    {
        Canvas.SetTop(LeftWaveBar, (IconCanvasHeight - LeftWaveBar.Height) / 2);
        Canvas.SetTop(CenterWaveBar, (IconCanvasHeight - CenterWaveBar.Height) / 2);
        Canvas.SetTop(RightWaveBar, (IconCanvasHeight - RightWaveBar.Height) / 2);
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
