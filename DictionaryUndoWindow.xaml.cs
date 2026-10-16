using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BetterFlow;

public partial class DictionaryUndoWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const double IndicatorHeight = 38;
    private const double IndicatorBottomMargin = 12;
    private const double Gap = 4;
    private readonly DispatcherTimer _hideTimer;

    public DictionaryUndoWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
            UndoExpired?.Invoke(this, EventArgs.Empty);
        };
        SourceInitialized += (_, _) => PreventActivation();
        Closed += (_, _) => _hideTimer.Stop();
    }

    public event EventHandler? UndoRequested;
    public event EventHandler? UndoExpired;

    public void ShowUndo()
    {
        _hideTimer.Stop();
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        var indicatorTop = SystemParameters.PrimaryScreenHeight - IndicatorHeight - IndicatorBottomMargin;
        Top = Math.Max(0, indicatorTop - Height - Gap);
        if (!IsVisible) Show();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.Start();
    }

    public void HideUndo()
    {
        _hideTimer.Stop();
        Hide();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        HideUndo();
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PreventActivation()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(currentStyle | WsExToolWindow | WsExNoActivate));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
