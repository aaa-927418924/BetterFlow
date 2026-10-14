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
    private string _pendingQuestion = string.Empty;
    private string _currentAssistantText = string.Empty;

    public AnswerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    public void ResetConversation()
    {
        RunOnUi(() =>
        {
            _followOutput = true;
            _conversationText = string.Empty;
            _pendingQuestion = string.Empty;
            _currentAssistantText = string.Empty;
            AnswerContent.Text = string.Empty;
            Hide();
        });
    }

    public void BeginConversationTurn(string question)
    {
        RunOnUi(() =>
        {
            _followOutput = true;
            CommitCurrentTurnCore();
            _pendingQuestion = question.Trim();
            _currentAssistantText = string.Empty;
            RefreshAnswerText();
        });
    }

    public void SetCurrentAssistantText(string text)
    {
        RunOnUi(() =>
        {
            _currentAssistantText = text;
            RefreshAnswerText();
            if (!string.IsNullOrWhiteSpace(text)) ShowAboveIndicator();
        });
    }

    public void CommitCurrentTurn()
    {
        RunOnUi(() =>
        {
            CommitCurrentTurnCore();
            RefreshAnswerText();
        });
    }

    public void DiscardCurrentTurn()
    {
        RunOnUi(() =>
        {
            _pendingQuestion = string.Empty;
            _currentAssistantText = string.Empty;
            RefreshAnswerText();
            if (string.IsNullOrWhiteSpace(_conversationText)) Hide();
        });
    }

    public void HideAnswer() => RunOnUi(Hide);

    private void CommitCurrentTurnCore()
    {
        if (!string.IsNullOrWhiteSpace(_currentAssistantText))
        {
            if (_conversationText.Length > 0) _conversationText += "\n\n";
            if (_pendingQuestion.Length > 0)
                _conversationText += $"あなた: {_pendingQuestion}\n\n回答: ";
            _conversationText += _currentAssistantText.Trim();
        }
        _pendingQuestion = string.Empty;
        _currentAssistantText = string.Empty;
    }

    private void RefreshAnswerText()
    {
        var currentTurn = string.IsNullOrWhiteSpace(_currentAssistantText)
            ? string.Empty
            : (_pendingQuestion.Length > 0
                ? $"あなた: {_pendingQuestion}\n\n回答: {_currentAssistantText}"
                : _currentAssistantText);
        AnswerContent.Text = _conversationText.Length > 0 && currentTurn.Length > 0
            ? _conversationText + "\n\n" + currentTurn
            : _conversationText + currentTurn;
        if (_followOutput) AnswerScrollViewer.ScrollToEnd();
    }

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
