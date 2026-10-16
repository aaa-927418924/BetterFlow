using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterFlow.Services;

namespace BetterFlow;

public partial class AnswerWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private string _currentAssistantText = string.Empty;
    private IReadOnlyList<AnswerSourceCard> _currentSources = Array.Empty<AnswerSourceCard>();

    public AnswerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeToolWindow();
    }

    public void ResetAnswer()
    {
        RunOnUi(() =>
        {
            _currentAssistantText = string.Empty;
            _currentSources = Array.Empty<AnswerSourceCard>();
            AnswerContent.Text = string.Empty;
            AnswerSourceItems.ItemsSource = _currentSources;
            SourceSection.Visibility = Visibility.Collapsed;
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
            var parsed = AnswerSourceParser.Parse(text);
            var nextText = parsed.Text;
            if (!forceReplace && nextText.Length < _currentAssistantText.Length &&
                _currentAssistantText.StartsWith(nextText, StringComparison.Ordinal) &&
                parsed.Sources.SequenceEqual(_currentSources))
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
            _currentSources = parsed.Sources;
            AnswerContent.Text = _currentAssistantText;
            AnswerSourceItems.ItemsSource = _currentSources;
            SourceSection.Visibility = _currentSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

            if (string.IsNullOrWhiteSpace(_currentAssistantText) && parsed.Sources.Count == 0)
                Hide();
            else
            {
                ShowAboveIndicator();
                AnswerScrollViewer.UpdateLayout();
                AnswerScrollViewer.ScrollToEnd();
            }
        });
    }

    public void ClearAnswer() => ResetAnswer();

    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AnswerSourceCard source } ||
            !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
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
