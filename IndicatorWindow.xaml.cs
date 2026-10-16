using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BetterFlow;

internal enum ProcessingProgressStage
{
    Recognition,
    Preparing,
    GeneratingOrApplying,
    Applying,
    Complete
}

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
    private const double MaximumProgressBeforeFirstText = 64;
    private const double MaximumProgressDuringGeneration = 95;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _progressTimer;
    private double _progressPercent;
    private double _progressTargetPercent;
    private ProcessingProgressStage? _activeProgressStage;
    private DateTime _activeStageStartedAt;
    private DateTime? _aiRequestStartedAt;
    private int _streamedTextLength;
    private bool _progressSessionActive;
    private bool _aiRequestActive;
    private bool _assistantTextSeen;
    private bool _progressComplete;

    public IndicatorWindow()
    {
        InitializeComponent();
        _progressTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _progressTimer.Tick += UpdateAnimatedProgress;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _progressTimer.Stop();
            ProgressFill.Visibility = Visibility.Collapsed;
            Hide();
        };
        Loaded += (_, _) => UpdateProgressFillWidth();
        SourceInitialized += (_, _) => MakeClickThrough();
        Closed += (_, _) =>
        {
            _hideTimer.Stop();
            _progressTimer.Stop();
        };
    }

    public void ShowStatus(string text, TimeSpan? autoHide = null)
    {
        _hideTimer.Stop();
        ResetProgress();
        ProgressFill.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        ShowAtBottom();

        if (autoHide is { } duration)
        {
            _hideTimer.Interval = duration;
            _hideTimer.Start();
        }
    }

    internal void ShowProcessingStatus(
        string text,
        ProcessingProgressStage stage,
        TimeSpan? autoHide = null)
    {
        _hideTimer.Stop();
        SetProgressStage(stage);
        StatusText.Text = text;
        ProgressFill.Visibility = Visibility.Visible;
        ShowAtBottom();
        _progressTimer.Start();
        UpdateProgressFillWidth();

        if (autoHide is { } duration)
        {
            _hideTimer.Interval = duration;
            _hideTimer.Start();
        }
    }

    internal void ReportOpenCodeProgress(Services.OpenCodeProgressUpdate update)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => ReportOpenCodeProgress(update)));
            return;
        }

        if (!_progressSessionActive || _progressComplete)
            return;

        var now = DateTime.UtcNow;
        switch (update.Kind)
        {
            case Services.OpenCodeProgressKind.RequestStarted:
                _aiRequestActive = true;
                _assistantTextSeen = false;
                _streamedTextLength = 0;
                _aiRequestStartedAt = now;
                _activeProgressStage = ProcessingProgressStage.GeneratingOrApplying;
                _activeStageStartedAt = now;
                AdvanceProgressTarget(45);
                break;
            case Services.OpenCodeProgressKind.ModelReady:
                AdvanceProgressTarget(48);
                break;
            case Services.OpenCodeProgressKind.PromptSubmitted:
                _aiRequestStartedAt = now;
                AdvanceProgressTarget(52);
                break;
            case Services.OpenCodeProgressKind.AssistantActivity:
                AdvanceProgressTarget(56);
                break;
            case Services.OpenCodeProgressKind.TextUpdated:
                ReportAssistantTextLength(update.TextLength);
                break;
        }

        _progressTimer.Start();
    }

    internal void ReportAssistantTextLength(int textLength)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => ReportAssistantTextLength(textLength)));
            return;
        }

        if (!_progressSessionActive || _progressComplete)
            return;
        if (textLength <= 0)
            return;

        if (_activeProgressStage != ProcessingProgressStage.GeneratingOrApplying)
        {
            _activeProgressStage = ProcessingProgressStage.GeneratingOrApplying;
            _activeStageStartedAt = DateTime.UtcNow;
            AdvanceProgressTarget(45);
        }
        _assistantTextSeen = true;
        _streamedTextLength = Math.Max(_streamedTextLength, Math.Max(0, textLength));
        var textTarget = 64 + 31 * (1 - Math.Exp(-_streamedTextLength / 450d));
        AdvanceProgressTarget(Math.Min(MaximumProgressDuringGeneration, textTarget));
        _progressTimer.Start();
    }

    private void SetProgressStage(ProcessingProgressStage stage)
    {
        var startsNewSession = !_progressSessionActive || _progressComplete ||
                               (stage == ProcessingProgressStage.Recognition &&
                                _activeProgressStage != ProcessingProgressStage.Recognition);
        if (startsNewSession)
            BeginProgressSession();

        if (_activeProgressStage != stage)
        {
            _activeProgressStage = stage;
            _activeStageStartedAt = DateTime.UtcNow;
        }

        switch (stage)
        {
            case ProcessingProgressStage.Recognition:
                _aiRequestActive = false;
                _aiRequestStartedAt = null;
                _assistantTextSeen = false;
                _streamedTextLength = 0;
                AdvanceProgressTarget(25);
                break;
            case ProcessingProgressStage.Preparing:
                AdvanceProgressTarget(34);
                break;
            case ProcessingProgressStage.GeneratingOrApplying:
                AdvanceProgressTarget(45);
                break;
            case ProcessingProgressStage.Applying:
                _aiRequestActive = false;
                _aiRequestStartedAt = null;
                AdvanceProgressTarget(96);
                break;
            case ProcessingProgressStage.Complete:
                _aiRequestActive = false;
                _aiRequestStartedAt = null;
                _progressComplete = true;
                AdvanceProgressTarget(100);
                break;
        }
    }

    private void BeginProgressSession()
    {
        _progressTimer.Stop();
        _progressSessionActive = true;
        _progressComplete = false;
        _progressPercent = 0;
        _progressTargetPercent = 0;
        _activeProgressStage = null;
        _activeStageStartedAt = DateTime.UtcNow;
        _aiRequestStartedAt = null;
        _streamedTextLength = 0;
        _aiRequestActive = false;
        _assistantTextSeen = false;
    }

    private void AdvanceProgressTarget(double targetPercent)
    {
        _progressTargetPercent = Math.Max(_progressTargetPercent,
            Math.Clamp(targetPercent, 0, 100));
    }

    private void UpdateAnimatedProgress(object? sender, EventArgs e)
    {
        if (!_progressSessionActive)
        {
            _progressTimer.Stop();
            return;
        }

        UpdateEstimatedProgressTarget(DateTime.UtcNow);

        var remaining = _progressTargetPercent - _progressPercent;
        var smoothing = _progressComplete ? 0.20 : 0.075;
        _progressPercent += remaining * smoothing;
        if (Math.Abs(_progressTargetPercent - _progressPercent) < 0.06)
            _progressPercent = _progressTargetPercent;

        UpdateProgressFillWidth();
        if (_progressComplete && _progressPercent >= 99.94)
            _progressTimer.Stop();
    }

    private void UpdateEstimatedProgressTarget(DateTime now)
    {
        var stageSeconds = Math.Max(0, (now - _activeStageStartedAt).TotalSeconds);
        if (_aiRequestActive && _aiRequestStartedAt is { } aiStartedAt)
        {
            var aiSeconds = Math.Max(0, (now - aiStartedAt).TotalSeconds);
            var elapsedBase = _assistantTextSeen ? 64d : 52d;
            var elapsedRange = _assistantTextSeen ? 24d : 12d;
            var elapsedTarget = elapsedBase + elapsedRange * (1 - Math.Exp(-aiSeconds / 35d));
            AdvanceProgressTarget(Math.Min(_assistantTextSeen ? 90 : MaximumProgressBeforeFirstText,
                elapsedTarget));
        }
        else if (_activeProgressStage == ProcessingProgressStage.Preparing)
        {
            AdvanceProgressTarget(Math.Min(43, 34 + stageSeconds * 0.25));
        }
        else if (_activeProgressStage == ProcessingProgressStage.GeneratingOrApplying)
        {
            // Other live-response paths do not expose OpenCode events; keep their
            // indicator moving slowly until streamed response text arrives.
            AdvanceProgressTarget(Math.Min(64, 45 + stageSeconds * 0.18));
        }
    }

    private void ResetProgress()
    {
        _progressTimer.Stop();
        _progressSessionActive = false;
        _progressComplete = false;
        _progressPercent = 0;
        _progressTargetPercent = 0;
        _activeProgressStage = null;
        _aiRequestStartedAt = null;
        _streamedTextLength = 0;
        _aiRequestActive = false;
        _assistantTextSeen = false;
    }

    private void ShowAtBottom()
    {
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - Height - 12);

        if (!IsVisible)
            Show();
    }

    private void IndicatorContent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateProgressFillWidth();

    private void UpdateProgressFillWidth()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateProgressFillWidth));
            return;
        }

        ProgressFill.Width = Math.Max(0, IndicatorContent.ActualWidth * _progressPercent / 100d);
    }

    public void HideStatus()
    {
        _hideTimer.Stop();
        ResetProgress();
        ProgressFill.Visibility = Visibility.Collapsed;
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
