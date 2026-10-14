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
    private bool _followOutput = true;
    private string _currentAssistantText = string.Empty;

    public AnswerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeToolWindow();
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

            var hadFocus = AnswerContent.IsKeyboardFocusWithin;
            var previousText = _currentAssistantText;
            var selectionStart = AnswerContent.SelectionStart;
            var selectionLength = AnswerContent.SelectionLength;
            var selectedAll = hadFocus && selectionLength == previousText.Length &&
                              selectionStart == 0 && previousText.Length > 0;
            var selectedText = hadFocus ? AnswerContent.SelectedText : string.Empty;
            var caretWasAtEnd = hadFocus && selectionLength == 0 &&
                                AnswerContent.CaretIndex == previousText.Length;

            _currentAssistantText = nextText;
            AnswerContent.Text = _currentAssistantText;

            if (hadFocus)
            {
                if (selectedAll && nextText.StartsWith(previousText, StringComparison.Ordinal))
                    AnswerContent.Select(0, nextText.Length);
                else if (selectionLength > 0 && selectionStart + selectionLength <= nextText.Length &&
                         nextText.AsSpan(selectionStart, selectionLength).SequenceEqual(selectedText.AsSpan()))
                    AnswerContent.Select(selectionStart, selectionLength);
                else if (caretWasAtEnd && nextText.StartsWith(previousText, StringComparison.Ordinal))
                    AnswerContent.CaretIndex = nextText.Length;
            }

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

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    private void MakeToolWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(currentStyle | WsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
