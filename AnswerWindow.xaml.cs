using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BetterFlow;

public partial class AnswerWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private bool _followOutput = true;
    private string _currentAssistantText = string.Empty;

    public AnswerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    public void ResetAnswer()
    {
        RunOnUi(() =>
        {
            _followOutput = true;
            _currentAssistantText = string.Empty;
            AnswerContent.Text = string.Empty;
            Hide();
        });
    }

    public void BeginAnswerTurn()
    {
        ResetAnswer();
    }

    public void SetCurrentAssistantText(string text)
    {
        SetAssistantText(text, forceReplace: false);
    }

    public void SetFinalAssistantText(string text)
    {
        SetAssistantText(text, forceReplace: true);
    }

    private void SetAssistantText(string text, bool forceReplace)
    {
        RunOnUi(() =>
        {
            var nextText = text ?? string.Empty;
            if (!forceReplace && nextText.Length < _currentAssistantText.Length &&
                _currentAssistantText.StartsWith(nextText, StringComparison.Ordinal))
                return;

            _currentAssistantText = nextText;
            AnswerContent.Text = _currentAssistantText;
            if (_followOutput) AnswerScrollViewer.ScrollToEnd();

            if (string.IsNullOrWhiteSpace(_currentAssistantText))
                Hide();
            else
                ShowAboveIndicator();
        });
    }

    public void ClearAnswer() => ResetAnswer();

    private void ShowAboveIndicator()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var indicatorHeight = 38d;
        var bottomGap = 12d;
        Left = Math.Max(0, (screenWidth - Width) / 2);
        Top = Math.Max(8, screenHeight - indicatorHeight - bottomGap - Height - 12);
        if (!IsVisible) Show();
    }

    private void AnswerScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _followOutput = AnswerScrollViewer.VerticalOffset + AnswerScrollViewer.ViewportHeight >=
                        AnswerScrollViewer.ExtentHeight - 24;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    private void MakeNonActivating()
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
