using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace BetterFlow;

public partial class TranscriptPreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const double IndicatorHeight = 38;
    private const double IndicatorBottomGap = 12;
    private const double GapAboveIndicator = 8;

    public TranscriptPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void ShowPreview(string text)
        => ShowPreviewCore(text, isVoiceCommand: false);

    public void ShowVoiceCommandPreview(string text)
        => ShowPreviewCore(text, isVoiceCommand: true);

    private void ShowPreviewCore(string text, bool isVoiceCommand)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(new Action(() => ShowPreviewCore(text, isVoiceCommand)));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        PreviewText.Text = text;
        PreviewHighlight.Background = isVoiceCommand
            ? new SolidColorBrush(MediaColor.FromRgb(255, 220, 115))
            : MediaBrushes.Transparent;
        PreviewHighlight.Padding = isVoiceCommand ? new Thickness(5, 3, 5, 3) : new Thickness(0);
        PreviewText.Foreground = isVoiceCommand
            ? new SolidColorBrush(MediaColor.FromRgb(38, 33, 20))
            : new SolidColorBrush(MediaColor.FromRgb(244, 244, 244));
        if (!IsVisible) Show();
        UpdateLayout();
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - IndicatorBottomGap -
            IndicatorHeight - GapAboveIndicator - ActualHeight);
    }

    public void HidePreview()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(new Action(HidePreview));
            return;
        }

        PreviewHighlight.Background = MediaBrushes.Transparent;
        PreviewHighlight.Padding = new Thickness(0);
        PreviewText.Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 244));
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
