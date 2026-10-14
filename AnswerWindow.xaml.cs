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
    private string _conversationText = string.Empty;
    private string _currentAssistantText = string.Empty;

    public AnswerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    public void BeginAnswer(string status)
    {
        RunOnUi(() =>
        {
            _followOutput = true;
            AnswerTitle.Text = "回答";
            _conversationText = string.Empty;
            _currentAssistantText = string.Empty;
            AnswerContent.Text = string.Empty;
            AnswerStatus.Text = status;
            ShowAboveIndicator();
        });
    }

    public void BeginConversationTurn(string question)
    {
        RunOnUi(() =>
        {
            _followOutput = true;
            if (_currentAssistantText.Length > 0)
                _conversationText += _currentAssistantText;
            if (_conversationText.Length > 0)
                _conversationText += "\n\n";
            _conversationText += $"あなた: {question}\n\n回答: ";
            _currentAssistantText = string.Empty;
            AnswerContent.Text = _conversationText;
            AnswerStatus.Text = "GPT Liveが回答しています…";
            ShowAboveIndicator();
            AnswerScrollViewer.ScrollToEnd();
        });
    }

    public void SetResponseText(string text)
    {
        RunOnUi(() =>
        {
            _conversationText = text;
            _currentAssistantText = string.Empty;
            AnswerContent.Text = _conversationText;
            if (_followOutput) AnswerScrollViewer.ScrollToEnd();
        });
    }

    public void SetCurrentAssistantText(string text)
    {
        RunOnUi(() =>
        {
            _currentAssistantText = text;
            AnswerContent.Text = _conversationText + _currentAssistantText;
            if (_followOutput) AnswerScrollViewer.ScrollToEnd();
        });
    }

    public void SetStatus(string status)
    {
        RunOnUi(() => AnswerStatus.Text = status);
    }

    public void HideAnswer() => RunOnUi(Hide);

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
