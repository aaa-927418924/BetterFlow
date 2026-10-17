using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    // Trial appearance only. False restores the saved blur without changing capture or UI behavior.
    private const bool EnablePrismTrial = true;
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
    private Services.IndicatorGlassBackdrop? _glassBackdrop;
    private double _progressPercent;
    private double _progressTargetPercent;
    private ProcessingProgressStage? _activeProgressStage;
    private DateTime _activeStageStartedAt;
    private DateTime? _aiRequestStartedAt;
    private DateTime _lastProgressFrameAt;
    private int _streamedTextLength;
    private bool _progressSessionActive;
    private bool _progressRenderingSubscribed;
    private bool _aiRequestActive;
    private bool _assistantTextSeen;
    private bool _progressComplete;
    private bool _operationHintsEnabled = true;
    private bool _usesDefaultRecordingHint;
    private string _activationShortcutLabel = "右Alt";
    private string _currentOperationHint = string.Empty;

    public IndicatorWindow()
    {
        InitializeComponent();
        PrismRim.Visibility = EnablePrismTrial && !SystemParameters.HighContrast &&
            RenderCapability.IsPixelShaderVersionSupported(3, 0) ? Visibility.Visible : Visibility.Collapsed;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            StopProgressAnimation();
            ProgressFill.Visibility = Visibility.Collapsed;
            _currentOperationHint = string.Empty;
            _usesDefaultRecordingHint = false;
            UpdateOperationHint();
            Hide();
        };
        Loaded += (_, _) => UpdateProgressFillWidth();
        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source is null) return;
            _glassBackdrop = new Services.IndicatorGlassBackdrop(source, GlassBackdropImage);
        };
        Closed += (_, _) =>
        {
            _hideTimer.Stop();
            StopProgressAnimation();
            _glassBackdrop?.Dispose();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _glassBackdrop?.ClearBackground();
        };
    }

    public void ShowStatus(string text, TimeSpan? autoHide = null, string? operationHint = null)
    {
        _hideTimer.Stop();
        ResetProgress();
        ProgressFill.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        _usesDefaultRecordingHint = operationHint is null && text == "録音中";
        _currentOperationHint = operationHint ??
            (text == "録音中" ? $"{_activationShortcutLabel}で確定" : string.Empty);
        UpdateOperationHint();
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
        TimeSpan? autoHide = null,
        string? operationHint = null)
    {
        _hideTimer.Stop();
        SetProgressStage(stage);
        StatusText.Text = text;
        _usesDefaultRecordingHint = false;
        _currentOperationHint = operationHint ?? string.Empty;
        UpdateOperationHint();
        ProgressFill.Visibility = Visibility.Visible;
        ShowAtBottom();
        StartProgressAnimation();
        UpdateProgressFillWidth();

        if (autoHide is { } duration)
        {
            _hideTimer.Interval = duration;
            _hideTimer.Start();
        }
    }

    internal void SetOperationHintSettings(bool enabled, string shortcutLabel)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => SetOperationHintSettings(enabled, shortcutLabel)));
            return;
        }

        _operationHintsEnabled = enabled;
        _activationShortcutLabel = string.IsNullOrWhiteSpace(shortcutLabel) ? "右Alt" : shortcutLabel;
        if (_usesDefaultRecordingHint)
            _currentOperationHint = $"{_activationShortcutLabel}で確定";
        UpdateOperationHint();
    }

    private void UpdateOperationHint()
    {
        var hint = _operationHintsEnabled ? _currentOperationHint : string.Empty;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
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

        StartProgressAnimation();
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
        StartProgressAnimation();
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
            StopProgressAnimation();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsedSeconds = _lastProgressFrameAt == default
            ? 1d / 60d
            : Math.Clamp((now - _lastProgressFrameAt).TotalSeconds, 0, 0.1);
        _lastProgressFrameAt = now;
        UpdateEstimatedProgressTarget(now);

        var remaining = _progressTargetPercent - _progressPercent;
        var smoothingDurationSeconds = _progressComplete ? 0.12 : 0.24;
        var smoothing = 1 - Math.Exp(-elapsedSeconds / smoothingDurationSeconds);
        _progressPercent += remaining * smoothing;
        if (Math.Abs(_progressTargetPercent - _progressPercent) < 0.06)
            _progressPercent = _progressTargetPercent;

        UpdateProgressFillWidth();
        if (_progressComplete && _progressPercent >= 99.94)
            StopProgressAnimation();
    }

    private void StartProgressAnimation()
    {
        if (_progressRenderingSubscribed)
            return;

        _lastProgressFrameAt = DateTime.UtcNow;
        CompositionTarget.Rendering += UpdateAnimatedProgress;
        _progressRenderingSubscribed = true;
    }

    private void StopProgressAnimation()
    {
        if (_progressRenderingSubscribed)
        {
            CompositionTarget.Rendering -= UpdateAnimatedProgress;
            _progressRenderingSubscribed = false;
        }

        _lastProgressFrameAt = default;
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
        StopProgressAnimation();
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
        {
            new WindowInteropHelper(this).EnsureHandle();
            _glassBackdrop?.PrepareBackground();
            Show();
        }
    }

    private void IndicatorContent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateProgressFillWidth();

    private void GlassRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = Math.Min(e.NewSize.Width, e.NewSize.Height) / 2;
        GlassRoot.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        GlassRefraction.SurfaceSize = new System.Windows.Point(e.NewSize.Width, e.NewSize.Height);
        PrismRefraction.SurfaceSize = new System.Windows.Point(e.NewSize.Width, e.NewSize.Height);
        if (!RenderCapability.IsPixelShaderVersionSupported(3, 0))
            LensBackdrop.Effect = null;
    }

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
        _currentOperationHint = string.Empty;
        _usesDefaultRecordingHint = false;
        UpdateOperationHint();
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
