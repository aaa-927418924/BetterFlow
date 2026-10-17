using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
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
    private static readonly SolidColorBrush RegularTextBrush = new(MediaColor.FromRgb(244, 244, 244));
    private static readonly SolidColorBrush VoiceCommandTextBrush = new(MediaColor.FromRgb(38, 33, 20));
    private static readonly SolidColorBrush VoiceCommandHighlightBrush = new(MediaColor.FromRgb(255, 220, 115));
    private Services.GlassBackdropSampler? _glassBackdrop;

    public TranscriptPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source is not null)
                _glassBackdrop = new Services.GlassBackdropSampler(source, GlassBackdropImage);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _glassBackdrop?.ClearBackground();
        };
        Closed += (_, _) => _glassBackdrop?.Dispose();
    }

    public void ShowPreview(string text)
        => ShowSegmentedPreview([new TranscriptPreviewPart(text, IsVoiceCommand: false)]);

    internal void ShowSegmentedPreview(IReadOnlyList<TranscriptPreviewPart> parts)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                var snapshot = parts.ToArray();
                Dispatcher.BeginInvoke(new Action(() => ShowSegmentedPreview(snapshot)));
            }
            return;
        }

        var visibleParts = parts
            .Where(part => part.IsLineBreakCommand || !string.IsNullOrWhiteSpace(part.Text))
            .ToArray();
        if (visibleParts.Length == 0)
        {
            HidePreview();
            return;
        }

        PreviewText.Inlines.Clear();
        string? previousText = null;
        foreach (var part in visibleParts)
        {
            if (part.IsLineBreakCommand)
            {
                PreviewText.Inlines.Add(new LineBreak());
                previousText = null;
                continue;
            }

            if (previousText is not null && NeedsAsciiWordSeparator(previousText, part.Text))
                PreviewText.Inlines.Add(new Run(" ") { Foreground = RegularTextBrush });

            PreviewText.Inlines.Add(part.IsVoiceCommand
                ? new Run(part.Text)
                {
                    Background = VoiceCommandHighlightBrush,
                    Foreground = VoiceCommandTextBrush
                }
                : new Run(part.Text) { Foreground = RegularTextBrush });
            previousText = part.Text;
        }

        UpdateLayout();
        PreviewScrollViewer.UpdateLayout();
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        var previewHeight = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        if (previewHeight <= 0)
        {
            GlassRoot.Measure(new System.Windows.Size(Width, MaxHeight));
            previewHeight = GlassRoot.DesiredSize.Height;
        }
        previewHeight = Math.Clamp(previewHeight, MinHeight, MaxHeight);
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - IndicatorBottomGap -
            IndicatorHeight - GapAboveIndicator - previewHeight);
        if (!IsVisible)
        {
            new WindowInteropHelper(this).EnsureHandle();
            _glassBackdrop?.PrepareBackground();
            Show();
        }
        UpdateLayout();
        PreviewScrollViewer.UpdateLayout();
        PreviewScrollViewer.ScrollToEnd();
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - IndicatorBottomGap -
            IndicatorHeight - GapAboveIndicator - ActualHeight);
    }

    private void GlassRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = Math.Min(19, Math.Min(e.NewSize.Width, e.NewSize.Height) / 2);
        GlassRoot.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    public void HidePreview()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(new Action(HidePreview));
            return;
        }

        PreviewText.Inlines.Clear();
        Hide();
    }

    private static bool NeedsAsciiWordSeparator(string previousText, string nextText)
    {
        var previous = previousText[^1];
        var next = nextText[0];
        return char.IsAsciiLetterOrDigit(previous) && char.IsAsciiLetterOrDigit(next);
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

internal sealed record TranscriptPreviewPart(
    string Text,
    bool IsVoiceCommand,
    bool IsLineBreakCommand = false);
