using System.IO;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using BetterFlow.Services;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace BetterFlow;

public partial class MainWindow : System.Windows.Window
{
    private const int RequiredIndependentDictionaryCorrections = 3;
    private static readonly TimeSpan PendingDictionaryEvidenceLifetime = TimeSpan.FromDays(90);
    private readonly SettingsStore _settingsStore = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly MicrophoneRecorder _recorder = new();
    private readonly ContinuousMicrophoneRecorder _continuousRecorder = new();
    private readonly DiscordPushToMuteController _discordPushToMute = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OpenCodeService _openCode = new();
    private readonly GptLiveAuthService _gptLiveAuth = new();
    private readonly ActivationSoundPlayer _activationSoundPlayer = new();
    private readonly IndicatorWindow _indicator;
    private readonly TranscriptPreviewWindow _transcriptPreviewWindow;
    private readonly UndoWindow _undoWindow;
    private readonly AnswerWindow _answerWindow;
    private readonly bool _startHiddenInTray;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _startupMenuItem;
    private System.Drawing.Icon? _ownedTrayIcon;
    private AsrService? _asr;
    private AppSettings _settings;
    private HwndSource? _source;
    private IntPtr _pasteTarget;
    private bool _isBusy;
    private bool _normalAudioInputActive;
    private volatile bool _voiceCommandModeActive;
    private bool _rawTranscriptFallbackRequested;
    private bool _closing;
    private bool _hotkeyRegistered;
    private bool _windowMessageHookAttached;
    private bool _exitRequested;
    private bool _syncingStartupMenu;
    private bool _isCapturingShortcut;
    private bool _isCapturingDiscordMuteShortcut;
    private bool _loadingDiscordMuteSettingsUi;
    private bool _isRefreshingOpenCodeModels;
    private bool _loadingOpenCodeModelUi;
    private bool _loadingMicrophoneDeviceUi;
    private bool _openCodeReady;
    private string? _recordingPrecedingTextContext;
    private string? _pendingRawTranscriptFallback;
    private CancellationTokenSource? _audioProcessingCancellation;
    private Task? _sendCommandListenerTask;
    private CancellationTokenSource? _transcriptPreviewCancellation;
    private Task? _transcriptPreviewTask;
    private IDisposable? _discordMuteLease;
    private long _transcriptPreviewSession;
    private AutoDictionaryLearningMonitor? _autoDictionaryLearningMonitor;
    private readonly SemaphoreSlim _dictionaryReviewSemaphore = new(1, 1);
    private CancellationTokenSource? _selectedTextQuestionCancellation;
    private GptLiveQuestionSession? _gptLiveQuestionSession;
    private readonly List<(string Question, string Answer)> _selectedTextQuestionHistory = new();
    private bool _selectedTextQuestionSessionActive;
    private bool _selectedTextQuestionSessionStopping;
    private bool _gptLiveQuestionActive;
    private bool _loadingSelectedTextAnswerModeUi;
    private bool _selectionEditSessionActive;
    private bool _selectionEditSessionStopping;
    private CancellationTokenSource? _selectionEditCancellation;
    private SelectionEditUndoRecord? _lastSelectionEditUndo;
    private IReadOnlyList<OpenCodeModelOption> _openCodeModels = Array.Empty<OpenCodeModelOption>();
    private IReadOnlyList<AudioInputDeviceOption> _audioInputDevices = Array.Empty<AudioInputDeviceOption>();

    private sealed record AutomaticDictionaryLearningResult(
        int AddedCount,
        int PendingUpdatedCount,
        int RemainingConfirmations,
        int SuppressedCount,
        int DuplicateSubmissionCount,
        int CapacitySkippedCount,
        int TotalPendingCount,
        bool SaveFailed);

    private sealed record DictionaryLearningCandidate(
        string PreferredSpelling,
        IReadOnlyList<string> MisrecognizedVariants);

    private sealed record SuppressedDictionaryTermOption(string Term)
    {
        public string AllowActionName => $"{Term}の自動登録を再度許可";
    }

    private sealed record SelectionEditUndoRecord(
        SelectionEditContext Context,
        string PreviousText,
        string AppliedText);

    private bool IsAnyRecording => _recorder.IsRecording || _continuousRecorder.IsRecording ||
                                   _selectionEditSessionActive || _gptLiveQuestionActive;

    public MainWindow()
    {
        InitializeComponent();
        _indicator = new IndicatorWindow();
        _transcriptPreviewWindow = new TranscriptPreviewWindow();
        _answerWindow = new AnswerWindow();
        _recorder.AudioLevelChanged += _indicator.SetAudioLevel;
        _continuousRecorder.AudioLevelChanged += _indicator.SetAudioLevel;
        _hotkey.VoiceCommandCaptureStartRequested += _recorder.BeginVoiceCommandCapture;
        _hotkey.VoiceCommandCaptureStopRequested += _recorder.EndVoiceCommandCapture;
        _undoWindow = new UndoWindow();
        _undoWindow.UndoRequested += UndoWindow_UndoRequested;
        _undoWindow.UndoExpired += UndoWindow_UndoExpired;
        _settings = _settingsStore.Load();
        _startHiddenInTray = Bootstrapper.IsModelReady();
        if (_startHiddenInTray)
        {
            TitleText.Text = "アプリを起動しています…";
            BodyText.Text = "音声モデルを確認しています。";
            CapacityText.Text = string.Empty;
        }
        NormalizeAiSettings();
        InitializeOpenCodeModelUi();
        RefreshCorrectionDictionaryItems();
        InitializeSelectedTextAnswerModeUi();
        InitializeMicrophoneDeviceUi();
        InitializeDiscordMuteSettingsUi();
        InitializeTrayIcon();
        Closing += MainWindow_Closing;
        SystemEvents.SessionEnding += SystemEvents_SessionEnding;
    }

    internal bool ShouldStartHiddenInTray => _startHiddenInTray;

    internal void StartHiddenInTray()
    {
        ShowInTaskbar = false;
        _ = InitializeExistingSetupAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AttachWindowMessageHook(new WindowInteropHelper(this).Handle);
    }

    private async Task InitializeExistingSetupAsync()
    {
        AppPaths.EnsureDirectories();
        if (!_startHiddenInTray) return;

        TitleText.Text = "アプリを起動しています…";
        BodyText.Text = "音声モデルを確認しています。";
        CapacityText.Text = string.Empty;

        ActionButton.IsEnabled = false;
        SetupStatus.Text = "音声モデルを確認しています…";
        SetupProgress.Visibility = System.Windows.Visibility.Visible;
        SetupProgress.IsIndeterminate = true;
        var currentStage = "音声モデルの起動";
        try
        {
            await LoadRecognizerAsync(_lifetime.Token);
            SetupStatus.Text = string.Empty;
        }
        catch (OperationCanceledException) when (_closing) { _indicator.HideStatus(); }
        catch (Exception ex)
        {
            TitleText.Text = "起動できませんでした。";
            BodyText.Text = "音声モデルを確認して、Setupから再試行してください。";
            CapacityText.Text = string.Empty;
            SetupStatus.Text = "Setupを押して再試行してください。";
            RuntimeStatus.Text = $"失敗した工程: {currentStage}\n{DescribeSetupError(ex)}";
        }
        finally
        {
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Visibility = System.Windows.Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        ScheduleOpenCodeWarmupIfNeeded();
    }

    private async void ActionButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedTextQuestionSessionActive)
        {
            await StopSelectedTextQuestionSessionAsync();
            return;
        }
        if (_selectionEditSessionActive)
        {
            await CancelAudioProcessingAsync();
            return;
        }
        if (_isBusy)
        {
            if (HandleNormalAudioProcessingShortcut())
                return;
            await CancelAudioProcessingAsync();
            return;
        }
        if (_asr is null)
        {
            await SetupAsync();
            return;
        }

        await RecordButton_Click();
    }

    private async Task SetupAsync()
    {
        _isBusy = true;
        ActionButton.IsEnabled = false;
        UpdateAiModeDisplay();
        SetupProgress.Visibility = System.Windows.Visibility.Visible;
        SetupProgress.IsIndeterminate = true;
        SetupProgress.Value = 0;
        SetupStatus.Text = "音声モデルを準備しています…";
        RuntimeStatus.Text = string.Empty;
        var currentStage = "音声モデルの取得";
        string? openCodeSetupIssue = null;
        try
        {
            var progress = new Progress<SetupProgress>(update =>
            {
                currentStage = update.Message.TrimEnd('…', '.');
                SetupStatus.Text = update.Message;
                SetupProgress.Value = Math.Clamp(update.Percent, 0, 100);
            });
            await new Bootstrapper().EnsureModelAsync(progress, _lifetime.Token);
            currentStage = "OpenCodeの準備";
            try
            {
                await new OpenCodeInstaller().EnsureInstalledAsync(progress, _lifetime.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                openCodeSetupIssue = DescribeSetupError(ex);
            }
            currentStage = "音声モデルの起動";
            SetupStatus.Text = "音声モデルを起動しています…";
            await LoadRecognizerAsync(_lifetime.Token);
            _settingsStore.Save(_settings);
            SetupProgress.Value = 100;
            SetupStatus.Text = string.Empty;
            RuntimeStatus.Text = openCodeSetupIssue is null
                ? string.Empty
                : "文字起こしは利用できます。OpenCodeの自動セットアップは完了しませんでした。AIモードを選ぶと再試行します。 " + openCodeSetupIssue;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            SetupStatus.Text = "セットアップできませんでした。内容を確認して、もう一度お試しください。";
            RuntimeStatus.Text = $"失敗した工程: {currentStage}\n{DescribeSetupError(ex)}";
        }
        finally
        {
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Visibility = System.Windows.Visibility.Collapsed;
            _isBusy = false;
            ActionButton.IsEnabled = true;
            if (_asr is null) ActionButton.Content = "Setup";
            UpdateAiModeDisplay();
        }

        ScheduleOpenCodeWarmupIfNeeded();
    }

    private static string DescribeSetupError(Exception exception)
    {
        var root = exception.GetBaseException();
        if (root is IOException io && (io.HResult & 0xFFFF) is 32 or 33)
            return "ファイルが別の処理で使用中です。実行中のセットアップがあれば完了を待ち、他のアプリで対象ファイルを開いていないか確認して再試行してください。\n" + root.Message;
        return root.Message;
    }

    private async Task EnsureOpenCodeReadyAsync(CancellationToken cancellationToken = default)
    {
        var effectiveToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetime.Token;
        var progress = new Progress<SetupProgress>(update => RuntimeStatus.Text = update.Message);
        await new OpenCodeInstaller().EnsureInstalledAsync(progress, effectiveToken);
        RuntimeStatus.Text = "OpenCodeを起動しています…";
        await Task.Run(() => _openCode.StartAsync(effectiveToken), effectiveToken);
    }

    private void ScheduleOpenCodeWarmupIfNeeded()
    {
        if (_closing || _asr is null || _openCodeReady || _isBusy)
            return;

        // Starting OpenCode on the first spoken request made that request absorb
        // server startup and provider discovery. Do that work while the app loads.
        _isBusy = true;
        UpdateAiModeDisplay();
        RuntimeStatus.Text = "OpenCodeモデルを起動しています…";
        _ = WarmOpenCodeForStartupAsync();
    }

    private async Task WarmOpenCodeForStartupAsync()
    {
        try
        {
            await EnsureOpenCodeReadyAsync();
            await LoadOpenCodeModelsAsync();
            var selectedModel = GetSelectedOpenCodeModel();
            if (selectedModel is null)
                throw new InvalidOperationException("OpenCodeのモデル一覧から使うモデルを選択してください。");

            await _openCode.EnsureModelAvailableAsync(
                selectedModel.ProviderId, selectedModel.ModelId, _lifetime.Token);
            SaveOpenCodeModelSelection(selectedModel);
            _settingsStore.Save(_settings);
            _openCodeReady = true;
            RuntimeStatus.Text = string.Empty;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            _openCodeReady = false;
            _openCode.Stop();
            RuntimeStatus.Text = "OpenCodeモデルを準備できませんでした。設定画面でモデルを選び直すか、一覧を更新してください。 " + ex.GetBaseException().Message;
        }
        finally
        {
            _isBusy = false;
            UpdateAiModeDisplay();
        }
    }

    private void UpdateAiModeDisplay()
    {
        var canChooseModel = _asr is not null && !_isBusy && !IsAnyRecording && !_isRefreshingOpenCodeModels;
        OpenCodeModelCombo.IsEnabled = canChooseModel;
        RefreshOpenCodeModelsButton.IsEnabled = canChooseModel && !_isRefreshingOpenCodeModels;
        CorrectionDictionarySettingsPanel.IsEnabled = _asr is not null && !_isBusy && !IsAnyRecording;
        UpdateDiscordMuteControlsEnabled();
        UpdateSelectedTextAnswerModeDisplay();
    }

    private void InitializeDiscordMuteSettingsUi()
    {
        _loadingDiscordMuteSettingsUi = true;
        try
        {
            DiscordMuteEnabledCheckBox.IsChecked = _settings.DiscordAutoMuteEnabled;
            UpdateDiscordMuteShortcutDisplay();
            DiscordMuteStatus.Text = !_settings.DiscordAutoMuteEnabled
                ? string.Empty
                : DiscordPushToMuteController.IsSupportedBinding(GetDiscordMuteShortcut())
                    ? "有効です。"
                    : "有効です。Discord側にキーを登録した後、Better Flowでも「変更」から同じキーを設定してください。";
        }
        finally { _loadingDiscordMuteSettingsUi = false; }
    }

    private void UpdateDiscordMuteShortcutDisplay()
    {
        var binding = GetDiscordMuteShortcut();
        var label = DiscordPushToMuteController.IsSupportedBinding(binding)
            ? GlobalHotkey.Format(binding)
            : "未設定";
        DiscordMuteShortcutCurrentText.Text = label;
        System.Windows.Automation.AutomationProperties.SetName(
            DiscordMuteShortcutChangeButton, $"Discordミュート用ショートカットを変更。現在: {label}");
    }

    private void UpdateDiscordMuteControlsEnabled()
    {
        var canChange = _asr is not null && !_isBusy && !IsAnyRecording;
        DiscordMuteEnabledCheckBox.IsEnabled = canChange && !_isCapturingDiscordMuteShortcut;
        DiscordMuteShortcutChangeButton.IsEnabled = canChange;
        DiscordMuteTutorialButton.IsEnabled = true;
    }

    private void DiscordMuteEnabledCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loadingDiscordMuteSettingsUi) return;

        var previous = _settings.DiscordAutoMuteEnabled;
        var requested = DiscordMuteEnabledCheckBox.IsChecked == true;
        if (_asr is null || _isBusy || IsAnyRecording)
        {
            _loadingDiscordMuteSettingsUi = true;
            DiscordMuteEnabledCheckBox.IsChecked = previous;
            _loadingDiscordMuteSettingsUi = false;
            DiscordMuteStatus.Text = "録音や処理を終えてから変更してください。";
            return;
        }

        _settings.DiscordAutoMuteEnabled = requested;
        try
        {
            _settingsStore.Save(_settings);
            if (!requested)
                DiscordMuteStatus.Text = "無効にしました。";
            else if (!DiscordPushToMuteController.IsSupportedBinding(GetDiscordMuteShortcut()))
                DiscordMuteStatus.Text = "有効にしました。Discord側にキーを登録した後、Better Flowでも「変更」から同じキーを設定してください。";
            else
                DiscordMuteStatus.Text = "有効にしました。";
        }
        catch (Exception ex)
        {
            _settings.DiscordAutoMuteEnabled = previous;
            _loadingDiscordMuteSettingsUi = true;
            DiscordMuteEnabledCheckBox.IsChecked = previous;
            _loadingDiscordMuteSettingsUi = false;
            DiscordMuteStatus.Text = "設定を保存できませんでした。";
            RuntimeStatus.Text = ex.Message;
        }
    }

    private HotkeyBinding GetDiscordMuteShortcut() =>
        new(_settings.DiscordMuteShortcutModifiers, _settings.DiscordMuteShortcutVirtualKey);

    private void DiscordMuteTutorialButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
        DiscordMuteTutorialPage.Visibility = System.Windows.Visibility.Visible;

    private void BackFromDiscordMuteTutorial_Click(object sender, System.Windows.RoutedEventArgs e) =>
        DiscordMuteTutorialPage.Visibility = System.Windows.Visibility.Collapsed;

    private void DiscordMuteShortcutChangeButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_asr is null || _isBusy || IsAnyRecording || _isCapturingShortcut || _isCapturingDiscordMuteShortcut)
            return;

        _isCapturingDiscordMuteShortcut = true;
        _hotkey.Unregister();
        _hotkeyRegistered = false;
        DiscordMuteShortcutChangeButton.Content = "入力中…";
        DiscordMuteShortcutCaptureStatus.Text = "Discordに登録したキーの組み合わせを押してください。右Alt単独またはF1～F24も使えます。Escでキャンセルします。";
        UpdateDiscordMuteControlsEnabled();
        DiscordMuteShortcutChangeButton.Focus();
    }

    private void HandleDiscordMuteShortcutCapture(Key key)
    {
        if (key == Key.Escape)
        {
            CancelDiscordMuteShortcutCapture();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var otherModifiers = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows;
        if (key == Key.RightAlt && (modifiers & otherModifiers) == ModifierKeys.None && !Keyboard.IsKeyDown(Key.LeftAlt))
        {
            ApplyDiscordMuteShortcut(new HotkeyBinding(0, HotkeyBinding.RightAltVirtualKey));
            return;
        }

        if (IsModifierKey(key))
        {
            DiscordMuteShortcutCaptureStatus.Text = $"{FormatPressedModifiers(modifiers)}を押しています。続けて組み合わせるキーを押してください。";
            return;
        }

        var binding = new HotkeyBinding(ToNativeModifiers(modifiers), (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!DiscordPushToMuteController.IsSupportedBinding(binding))
        {
            DiscordMuteShortcutCaptureStatus.Text = "修飾キーとの組み合わせ、右Alt単独、またはF1～F24を設定してください。";
            return;
        }

        ApplyDiscordMuteShortcut(binding);
    }

    private void ApplyDiscordMuteShortcut(HotkeyBinding binding)
    {
        var appShortcut = GetConfiguredHotkey();
        if (binding == appShortcut)
        {
            DiscordMuteShortcutCaptureStatus.Text = "Better Flowの録音キーと重なっています。別のキーを設定してください。";
            return;
        }

        var oldModifiers = _settings.DiscordMuteShortcutModifiers;
        var oldVirtualKey = _settings.DiscordMuteShortcutVirtualKey;
        _settings.DiscordMuteShortcutModifiers = binding.Modifiers;
        _settings.DiscordMuteShortcutVirtualKey = binding.VirtualKey;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.DiscordMuteShortcutModifiers = oldModifiers;
            _settings.DiscordMuteShortcutVirtualKey = oldVirtualKey;
            DiscordMuteStatus.Text = "ショートカットを保存できませんでした。";
            RuntimeStatus.Text = ex.Message;
            CancelDiscordMuteShortcutCapture(announceCancellation: false);
            return;
        }

        _isCapturingDiscordMuteShortcut = false;
        DiscordMuteShortcutChangeButton.Content = "変更";
        UpdateDiscordMuteShortcutDisplay();
        DiscordMuteShortcutCaptureStatus.Text = string.Empty;
        DiscordMuteStatus.Text = "キーを保存しました。";
        RegisterConfiguredHotkey();
        UpdateDiscordMuteControlsEnabled();
    }

    private void CancelDiscordMuteShortcutCapture(bool announceCancellation = true)
    {
        if (!_isCapturingDiscordMuteShortcut) return;
        _isCapturingDiscordMuteShortcut = false;
        DiscordMuteShortcutChangeButton.Content = "変更";
        DiscordMuteShortcutCaptureStatus.Text = announceCancellation ? "変更をキャンセルしました。" : string.Empty;
        RegisterConfiguredHotkey();
        UpdateDiscordMuteControlsEnabled();
    }

    private void SettingsComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.IsDropDownOpen) return;

        e.Handled = true;
        var scrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(comboBox);
        if (scrollViewer is null) return;

        if (e.Delta > 0) scrollViewer.LineUp();
        else scrollViewer.LineDown();
    }

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        var current = element;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void InitializeSelectedTextAnswerModeUi()
    {
        var mode = _settings.SelectedTextAnswerMode;
        if (mode is not ("opencode" or "gpt-live"))
        {
            _settings.SelectedTextAnswerMode = "opencode";
            _settingsStore.Save(_settings);
        }

        _loadingSelectedTextAnswerModeUi = true;
        try
        {
            SelectedTextAnswerModeCombo.SelectedItem = SelectedTextAnswerModeCombo.Items
                .OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.SelectedTextAnswerMode,
                    StringComparison.Ordinal));
        }
        finally { _loadingSelectedTextAnswerModeUi = false; }
        UpdateSelectedTextAnswerModeDisplay();
    }

    private void UpdateSelectedTextAnswerModeDisplay()
    {
        var isLiveMode = string.Equals(_settings.SelectedTextAnswerMode, "gpt-live", StringComparison.Ordinal);
        var signedIn = _gptLiveAuth.IsSignedIn;
        var canChange = !_isBusy && !IsAnyRecording;
        SelectedTextAnswerModeCombo.IsEnabled = canChange;
        GptLiveSignInPanel.Visibility = isLiveMode
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        GptLiveSignInStatus.Text = signedIn ? "サインイン済み" : "未サインイン";
        GptLiveSignInButton.Visibility = signedIn
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        GptLiveSignOutButton.Visibility = signedIn
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        GptLiveSignInButton.IsEnabled = canChange;
        GptLiveSignOutButton.IsEnabled = canChange;
    }

    private void SelectedTextAnswerModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSelectedTextAnswerModeUi || SelectedTextAnswerModeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string mode || mode is not ("opencode" or "gpt-live"))
            return;

        var previous = _settings.SelectedTextAnswerMode;
        _settings.SelectedTextAnswerMode = mode;
        try
        {
            _settingsStore.Save(_settings);
            RuntimeStatus.Text = mode == "gpt-live" && !_gptLiveAuth.IsSignedIn
                ? "GPT Liveを使うにはChatGPTへサインインしてください。"
                : "選択文章への回答方法を保存しました。";
        }
        catch (Exception ex)
        {
            _settings.SelectedTextAnswerMode = previous;
            _loadingSelectedTextAnswerModeUi = true;
            SelectedTextAnswerModeCombo.SelectedItem = SelectedTextAnswerModeCombo.Items
                .OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, previous, StringComparison.Ordinal));
            _loadingSelectedTextAnswerModeUi = false;
            RuntimeStatus.Text = "回答方法を保存できませんでした。 " + ex.Message;
        }
        UpdateSelectedTextAnswerModeDisplay();
    }

    private async void GptLiveSignInButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording) return;
        _isBusy = true;
        UpdateAiModeDisplay();
        GptLiveSignInStatus.Text = "ブラウザーでChatGPTにサインインしてください…";
        try
        {
            await _gptLiveAuth.SignInAsync(_lifetime.Token);
            GptLiveSignInStatus.Text = "サインイン済み";
            RuntimeStatus.Text = "GPT Liveにサインインしました。";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            GptLiveSignInStatus.Text = "サインインできませんでした。";
            RuntimeStatus.Text = ex.GetBaseException().Message;
        }
        finally
        {
            _isBusy = false;
            UpdateAiModeDisplay();
        }
    }

    private void GptLiveSignOutButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording) return;
        try
        {
            _gptLiveAuth.SignOut();
            GptLiveSignInStatus.Text = "未サインイン";
            RuntimeStatus.Text = "GPT Liveからサインアウトしました。";
        }
        catch (Exception ex) { RuntimeStatus.Text = "サインアウトできませんでした。 " + ex.Message; }
        UpdateSelectedTextAnswerModeDisplay();
    }

    private void NormalizeAiSettings()
    {
        var shouldSaveSettings = !File.Exists(AppPaths.SettingsFile);
        var resolveLegacyModelById = false;
        var hadLegacySettings = _settings.LegacyAiProviderId is not null || _settings.LegacyAiProviders is not null;
        if (hadLegacySettings)
        {
            var oldProviderId = _settings.LegacyAiProviderId ?? string.Empty;
            var oldSettings = _settings.LegacyAiProviders is not null &&
                              _settings.LegacyAiProviders.TryGetValue(oldProviderId, out var providerSettings)
                ? providerSettings
                : null;
            var oldModelId = oldSettings?.ModelId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelId) && !string.IsNullOrWhiteSpace(oldModelId))
            {
                _settings.OpenCodeModelId = oldModelId;
                _settings.OpenCodeModelProviderId = oldProviderId switch
                {
                    "opencode-zen-free" => "opencode",
                    "deepseek" => "deepseek",
                    "openrouter" => "openrouter",
                    "gemini" => "google",
                    "openai-compatible" => ResolveLegacyModelProvider(),
                    _ => "opencode"
                };
            }
            else if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelId))
            {
                _settings.OpenCodeModelProviderId = "opencode";
                _settings.OpenCodeModelId = "deepseek-v4.1-flash-free";
            }

            _settings.LegacyAiProviderId = null;
            _settings.LegacyAiProviders = null;
            shouldSaveSettings = true;
        }

        // AI formatting is now always enabled; older transcription-only settings
        // are migrated once so existing installs follow the same behavior.
        if (!_settings.AiModeEnabled)
        {
            _settings.AiModeEnabled = true;
            shouldSaveSettings = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelProviderId) && !resolveLegacyModelById)
        {
            _settings.OpenCodeModelProviderId = "opencode";
            shouldSaveSettings = true;
        }
        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelId))
        {
            _settings.OpenCodeModelId = "deepseek-v4.1-flash-free";
            shouldSaveSettings = true;
        }
        if (!string.Equals(_settings.SelectionEditEngine, "opencode", StringComparison.Ordinal))
        {
            _settings.SelectionEditEngine = "opencode";
            shouldSaveSettings = true;
        }

        if (_settings.LegacyDictionaryLearningMode is not null)
        {
            // The former speed/precision selector is retired. Automatic learning now
            // always uses the selected OpenCode model, so the old value is discarded.
            _settings.LegacyDictionaryLearningMode = null;
            shouldSaveSettings = true;
        }

        _settings.CorrectionDictionary = (_settings.CorrectionDictionary ?? new List<CorrectionDictionaryEntry>())
            .Where(entry => entry is not null)
            .Select(entry => new CorrectionDictionaryEntry
            {
                PreferredSpelling = CleanDictionaryField(entry.PreferredSpelling, 100),
                Alternatives = CleanDictionaryAlternatives(entry.Alternatives, entry.PreferredSpelling),
                Source = entry.Source is "automatic" ? "automatic" : "manual",
                UseCount = Math.Max(0, entry.UseCount),
                LastUsedUtc = entry.LastUsedUtc
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PreferredSpelling))
            .GroupBy(entry => entry.PreferredSpelling, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(100)
            .ToList();

        _settings.PendingDictionaryLearning = (_settings.PendingDictionaryLearning ?? new List<DictionaryLearningEvidence>())
            .Where(entry => entry is not null && entry.LastCorrectionUtc >= DateTimeOffset.UtcNow.AddDays(-90))
            .Select(entry => new DictionaryLearningEvidence
            {
                PreferredSpelling = CleanDictionaryField(entry.PreferredSpelling, 100),
                Alternatives = CleanDictionaryAlternatives(entry.Alternatives, entry.PreferredSpelling),
                IndependentCorrectionCount = Math.Clamp(entry.IndependentCorrectionCount, 1, 2),
                LastCorrectionUtc = entry.LastCorrectionUtc,
                CountedSubmissionIds = (entry.CountedSubmissionIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 80)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToList()
            })
            .Where(entry => entry.PreferredSpelling.Length >= 2 &&
                            entry.PreferredSpelling.Any(char.IsLetterOrDigit))
            .GroupBy(entry => entry.PreferredSpelling, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LastCorrectionUtc).First())
            .Take(100)
            .ToList();
        _settings.SuppressedAutomaticDictionaryTerms = (_settings.SuppressedAutomaticDictionaryTerms ?? new List<string>())
            .Select(term => CleanDictionaryField(term, 100))
            .Where(term => term.Length >= 2 && term.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(1_000)
            .ToList();

        if (shouldSaveSettings)
            _settingsStore.Save(_settings);

        string ResolveLegacyModelProvider()
        {
            resolveLegacyModelById = true;
            return string.Empty;
        }
    }

    private void InitializeOpenCodeModelUi()
    {
        _loadingOpenCodeModelUi = true;
        var selected = FindSelectedOpenCodeModel();
        OpenCodeModelCombo.ItemsSource = GetOpenCodeModelItems();
        OpenCodeModelCombo.SelectedItem = selected;
        OpenCodeModelStatus.Text = selected is null
            ? "OpenCode Desktopで現在接続中のproviderのモデルだけを一覧更新して読み込みます。"
            : string.Empty;
        _loadingOpenCodeModelUi = false;
    }

    private void InitializeMicrophoneDeviceUi() => RefreshMicrophoneDeviceList(updateStatus: false);

    private void RefreshMicrophoneDeviceList(bool updateStatus)
    {
        try
        {
            _audioInputDevices = MicrophoneRecorder.GetInputDevices();
            _loadingMicrophoneDeviceUi = true;
            MicrophoneDeviceCombo.ItemsSource = _audioInputDevices;
            var selected = _audioInputDevices.FirstOrDefault(device =>
                string.Equals(device.Id, _settings.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase));
            MicrophoneDeviceCombo.SelectedItem = selected ?? _audioInputDevices.FirstOrDefault(device =>
                string.Equals(device.Id, AudioInputDeviceOption.SystemDefaultId, StringComparison.Ordinal));
            _loadingMicrophoneDeviceUi = false;

            if (updateStatus)
            {
                MicrophoneDeviceStatus.Text = selected is null &&
                                              _settings.MicrophoneDeviceId != AudioInputDeviceOption.SystemDefaultId
                    ? "保存済みのマイクが見つかりません。システム設定のマイクを使用します。再接続後に一覧を再読み込みできます。"
                    : $"{_audioInputDevices.Count - 1}個の入力デバイスを確認しました。";
            }
        }
        catch (Exception ex)
        {
            _loadingMicrophoneDeviceUi = true;
            MicrophoneDeviceCombo.ItemsSource = new[]
            {
                new AudioInputDeviceOption(AudioInputDeviceOption.SystemDefaultId, -1, "システム設定のマイク (既定)")
            };
            MicrophoneDeviceCombo.SelectedIndex = 0;
            _loadingMicrophoneDeviceUi = false;
            if (updateStatus)
                MicrophoneDeviceStatus.Text = "入力デバイス一覧を読み込めませんでした。 " + ex.Message;
        }
    }

    private void MicrophoneDeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingMicrophoneDeviceUi || MicrophoneDeviceCombo.SelectedItem is not AudioInputDeviceOption selected)
            return;

        var previousDeviceId = _settings.MicrophoneDeviceId;
        _settings.MicrophoneDeviceId = selected.Id;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.MicrophoneDeviceId = previousDeviceId;
            _loadingMicrophoneDeviceUi = true;
            MicrophoneDeviceCombo.SelectedItem = _audioInputDevices.FirstOrDefault(device =>
                string.Equals(device.Id, previousDeviceId, StringComparison.OrdinalIgnoreCase)) ??
                _audioInputDevices.FirstOrDefault(device => device.Id == AudioInputDeviceOption.SystemDefaultId);
            _loadingMicrophoneDeviceUi = false;
            MicrophoneDeviceStatus.Text = "マイク設定を保存できませんでした。 " + ex.Message;
            return;
        }
        MicrophoneDeviceStatus.Text = selected.DeviceNumber < 0
            ? "Windowsのシステム設定にある既定のマイクを使用します。"
            : string.Empty;
    }

    private void RefreshMicrophoneDevices_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        RefreshMicrophoneDeviceList(updateStatus: true);
    }

    private void MicrophoneDeviceCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (MicrophoneDeviceCombo.Template.FindName("PopupBorder", MicrophoneDeviceCombo) is not
            System.Windows.Controls.Border popupBorder)
            return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(MicrophoneDeviceCombo).PixelsPerDip;
        var typeface = new Typeface(MicrophoneDeviceCombo.FontFamily, System.Windows.FontStyles.Normal,
            MicrophoneDeviceCombo.FontWeight, MicrophoneDeviceCombo.FontStretch);
        var widestName = _audioInputDevices
            .Select(device => new FormattedText(device.DisplayName, CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, typeface, MicrophoneDeviceCombo.FontSize,
                System.Windows.Media.Brushes.White, pixelsPerDip).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0)
            .Max();
        var desiredWidth = Math.Max(MicrophoneDeviceCombo.ActualWidth, widestName + 56);
        var screenLimit = Math.Max(MicrophoneDeviceCombo.ActualWidth,
            System.Windows.SystemParameters.WorkArea.Width - 32);
        popupBorder.Width = Math.Min(desiredWidth, screenLimit);
    }

    private void RefreshCorrectionDictionaryItems()
    {
        CorrectionDictionaryItems.ItemsSource = null;
        CorrectionDictionaryItems.ItemsSource = _settings.CorrectionDictionary.ToArray();
        var registeredTerms = new HashSet<string>(_settings.CorrectionDictionary.Select(entry => entry.PreferredSpelling),
            StringComparer.OrdinalIgnoreCase);
        var suppressed = _settings.SuppressedAutomaticDictionaryTerms
            .Where(term => !registeredTerms.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => new SuppressedDictionaryTermOption(term))
            .ToArray();
        SuppressedDictionaryItems.ItemsSource = null;
        SuppressedDictionaryItems.ItemsSource = suppressed;
        SuppressedDictionarySection.Visibility = suppressed.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        DictionaryListEmptyText.Visibility = _settings.CorrectionDictionary.Count == 0 && suppressed.Length == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void AllowSuppressedDictionaryTerm_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording || sender is not System.Windows.Controls.Button { Tag: string term })
            return;

        var previous = _settings.SuppressedAutomaticDictionaryTerms.ToList();
        var removed = _settings.SuppressedAutomaticDictionaryTerms.RemoveAll(suppressed =>
            string.Equals(suppressed, term, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;

        try
        {
            _settingsStore.Save(_settings);
            RefreshCorrectionDictionaryItems();
            DictionaryListStatus.Text = $"{term}の再学習を許可しました。次に手修正した文章を送信すると候補を判定します。";
        }
        catch (Exception ex)
        {
            _settings.SuppressedAutomaticDictionaryTerms = previous;
            RefreshCorrectionDictionaryItems();
            DictionaryListStatus.Text = "再学習の設定を保存できませんでした。 " + ex.Message;
        }
    }

    private void OpenDictionaryList_Click(object sender, System.Windows.RoutedEventArgs e) =>
        DictionaryListPage.Visibility = System.Windows.Visibility.Visible;

    private void BackFromDictionaryList_Click(object sender, System.Windows.RoutedEventArgs e) =>
        DictionaryListPage.Visibility = System.Windows.Visibility.Collapsed;

    private void StartAutoDictionaryLearningMonitor(string pastedText)
    {
        _autoDictionaryLearningMonitor?.Dispose();
        _autoDictionaryLearningMonitor = AutoDictionaryLearningMonitor.TryStart(_pasteTarget, pastedText);
        if (_autoDictionaryLearningMonitor is null)
        {
            CorrectionDictionaryStatus.Text = "入力欄の監視を開始できず、自動登録を待機できません。";
            return;
        }

        _autoDictionaryLearningMonitor.SubmissionConfirmed += AutoDictionaryLearningMonitor_SubmissionConfirmed;
        _autoDictionaryLearningMonitor.StatusChanged += AutoDictionaryLearningMonitor_StatusChanged;
        CorrectionDictionaryStatus.Text = "音声入力した文章を送信するまで監視しています。";
    }

    private void AutoDictionaryLearningMonitor_StatusChanged(
        object? sender,
        DictionaryLearningStatusUpdate update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing && sender is AutoDictionaryLearningMonitor monitor &&
                ReferenceEquals(_autoDictionaryLearningMonitor, monitor) &&
                string.Equals(update.MonitorId, monitor.MonitorId, StringComparison.Ordinal))
                CorrectionDictionaryStatus.Text = update.Message;
        }));
    }

    private void AutoDictionaryLearningMonitor_SubmissionConfirmed(
        object? sender,
        DictionaryCorrectionSubmission submission)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (sender is AutoDictionaryLearningMonitor monitor)
            {
                monitor.SubmissionConfirmed -= AutoDictionaryLearningMonitor_SubmissionConfirmed;
                monitor.StatusChanged -= AutoDictionaryLearningMonitor_StatusChanged;
                if (ReferenceEquals(_autoDictionaryLearningMonitor, monitor))
                    _autoDictionaryLearningMonitor = null;
                monitor.Dispose();
            }
            if (!_closing) _ = LearnDictionaryCorrectionAsync(submission);
        }));
    }

    private async Task LearnDictionaryCorrectionAsync(DictionaryCorrectionSubmission submission)
    {
        if (_closing)
        {
            DictionaryLearningDiagnosticLog.Write("learning_skipped", "closing",
                monitorId: submission.MonitorId, candidateId: submission.CandidateId);
            return;
        }
        var changes = DictionaryCorrectionCandidates.ExtractChanges(
            submission.OriginalText, submission.CorrectedText);
        DictionaryLearningDiagnosticLog.Write("candidates_extracted", changes.Count == 0 ? "none" : "found",
            changes.Count, submission.MonitorId, submission.CandidateId);
        if (changes.Count == 0)
        {
            CorrectionDictionaryStatus.Text = "単語単位の修正候補を特定できず、辞書は変更していません。";
            return;
        }

        if (!_openCodeReady || !_openCode.IsRunning)
        {
            var skipReason = !_openCodeReady ? "model-not-ready" : "opencode-not-running";
            DictionaryLearningDiagnosticLog.Write("learning_skipped", skipReason, changes.Count,
                submission.MonitorId, submission.CandidateId);
            CorrectionDictionaryStatus.Text = "OpenCodeモデルが準備できていないため、今回は登録を見送りました。";
            return;
        }

        var providerId = _settings.OpenCodeModelProviderId;
        var modelId = _settings.OpenCodeModelId;
        var cancellationToken = _lifetime.Token;
        var semaphoreAcquired = false;
        try
        {
            DictionaryLearningDiagnosticLog.Write("model_review_queued", "selected-model", changes.Count,
                submission.MonitorId, submission.CandidateId);
            await _dictionaryReviewSemaphore.WaitAsync(cancellationToken);
            semaphoreAcquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            DictionaryLearningDiagnosticLog.Write("model_review_started", "selected-model", changes.Count,
                submission.MonitorId, submission.CandidateId);
            var review = await _openCode.ReviewDictionaryCorrectionsAsync(
                changes, providerId, modelId, cancellationToken);
            var confirmedChanges = review.Items
                .Where(item => item.Decision == DictionaryCorrectionDecision.Confirmed &&
                               item.Index >= 0 && item.Index < changes.Count)
                .Select(item => changes[item.Index])
                .ToArray();
            var pendingChanges = review.Items
                .Where(item => item.Decision == DictionaryCorrectionDecision.Pending &&
                               item.Index >= 0 && item.Index < changes.Count)
                .Select(item => changes[item.Index])
                .ToArray();
            var pendingCount = review.Items.Count(item => item.Decision == DictionaryCorrectionDecision.Pending);
            var rejectedCount = review.Items.Count(item => item.Decision == DictionaryCorrectionDecision.Rejected);
            var reasonSummary = string.Join(",", review.Items
                .GroupBy(item => item.Reason)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}:{group.Count()}"));
            var reviewDetail = $"{review.OutputReason};confirmed:{confirmedChanges.Length};pending:{pendingCount};rejected:{rejectedCount}";
            if (reasonSummary.Length > 0)
                reviewDetail += ";reasons:" + reasonSummary;
            DictionaryLearningDiagnosticLog.Write("model_review_completed", reviewDetail,
                review.Items.Count, submission.MonitorId, submission.CandidateId);

            cancellationToken.ThrowIfCancellationRequested();
            var submissionId = $"{submission.MonitorId}:{submission.CandidateId}";
            var result = ApplyAutomaticDictionaryLearning(
                confirmedChanges, pendingChanges, submissionId, submission.MonitorId, submission.CandidateId);
            if (result.SaveFailed)
            {
                DictionaryLearningDiagnosticLog.Write("dictionary_save_failed", "settings-write-failed",
                    confirmedChanges.Length + pendingChanges.Length, submission.MonitorId, submission.CandidateId);
                CorrectionDictionaryStatus.Text = "辞書を保存できませんでした。設定ファイルを確認してください。";
                return;
            }

            var resultDetail = $"added:{result.AddedCount};pending-updated:{result.PendingUpdatedCount};" +
                               $"pending-total:{result.TotalPendingCount};suppressed:{result.SuppressedCount};" +
                               $"duplicate:{result.DuplicateSubmissionCount};capacity:{result.CapacitySkippedCount}";
            DictionaryLearningDiagnosticLog.Write("learning_evidence_updated", resultDetail,
                result.AddedCount + result.PendingUpdatedCount, submission.MonitorId, submission.CandidateId);

            if (result.AddedCount > 0)
            {
                DictionaryLearningDiagnosticLog.Write("learning_completed", "saved", result.AddedCount,
                    submission.MonitorId, submission.CandidateId);
                CorrectionDictionaryStatus.Text = result.AddedCount == 1
                    ? "送信した正しい表記と誤認識例を辞書に登録しました。"
                    : $"送信した修正から表記と誤認識例を{result.AddedCount}件登録しました。";
                ShowDictionarySavedIndicator();
            }
            else if (result.PendingUpdatedCount > 0)
            {
                var remaining = Math.Max(1, result.RemainingConfirmations);
                CorrectionDictionaryStatus.Text = $"用語候補を保留しました。同じ表記への独立した修正をあと{remaining}回確認すると登録します。";
            }
            else if (result.SuppressedCount > 0)
            {
                CorrectionDictionaryStatus.Text = "削除済みの用語は自動登録しませんでした。";
            }
            else if (pendingCount > 0 && result.DuplicateSubmissionCount > 0)
            {
                CorrectionDictionaryStatus.Text = "同じ送信の重複は学習回数に含めませんでした。";
            }
            else
            {
                DictionaryLearningDiagnosticLog.Write("learning_completed", "no-new-entry", confirmedChanges.Length,
                    submission.MonitorId, submission.CandidateId);
                CorrectionDictionaryStatus.Text = confirmedChanges.Length == 0 && pendingCount == 0
                    ? "選択モデルが登録可能な用語修正を承認しませんでした。辞書は変更していません。"
                    : result.CapacitySkippedCount > 0
                        ? "辞書の登録上限に達したため、候補を追加できませんでした。"
                        : "候補は確認しましたが、新しい辞書項目は追加されませんでした。既登録の可能性があります。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DictionaryLearningDiagnosticLog.Write("learning_failed", ex.GetType().Name, changes.Count,
                submission.MonitorId, submission.CandidateId);
            CorrectionDictionaryStatus.Text = "自動登録の判定に失敗しました。辞書は変更していません。詳細は診断ログを確認してください。";
        }
        finally
        {
            if (semaphoreAcquired) _dictionaryReviewSemaphore.Release();
        }
    }

    private AutomaticDictionaryLearningResult ApplyAutomaticDictionaryLearning(
        IEnumerable<DictionaryTermChange> confirmedChanges,
        IEnumerable<DictionaryTermChange> pendingChanges,
        string submissionId,
        string monitorId,
        long candidateId)
    {
        var previousDictionary = CloneCorrectionDictionary();
        var previousEvidence = CloneDictionaryLearningEvidence();
        var previousSuppressedTerms = _settings.SuppressedAutomaticDictionaryTerms.ToList();
        var changed = false;
        var addedCount = 0;
        var pendingUpdatedCount = 0;
        var remainingConfirmations = 0;
        var suppressedCount = 0;
        var duplicateCount = 0;
        var capacityCount = 0;
        var confirmed = PrepareDictionaryLearningCandidates(confirmedChanges);
        var confirmedSet = new HashSet<string>(confirmed.Select(candidate => candidate.PreferredSpelling),
            StringComparer.OrdinalIgnoreCase);
        var pending = PrepareDictionaryLearningCandidates(pendingChanges)
            .Where(candidate => !confirmedSet.Contains(candidate.PreferredSpelling))
            .ToArray();

        foreach (var candidate in confirmed)
        {
            var term = candidate.PreferredSpelling;
            changed |= RemovePendingEvidence(term);
            if (IsSuppressedAutomaticTerm(term))
            {
                suppressedCount++;
                continue;
            }
            var existing = _settings.CorrectionDictionary.FirstOrDefault(entry =>
                string.Equals(entry.PreferredSpelling, term, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var merged = MergeDictionaryAlternatives(existing.Alternatives, candidate.MisrecognizedVariants, term);
                if (!string.Equals(existing.Alternatives, merged, StringComparison.Ordinal))
                {
                    existing.Alternatives = merged;
                    changed = true;
                    addedCount++;
                }
                continue;
            }
            if (_settings.CorrectionDictionary.Count >= 100)
            {
                capacityCount++;
                continue;
            }

            _settings.CorrectionDictionary.Add(new CorrectionDictionaryEntry
            {
                PreferredSpelling = term,
                Alternatives = MergeDictionaryAlternatives(string.Empty, candidate.MisrecognizedVariants, term),
                Source = "automatic"
            });
            addedCount++;
            changed = true;
        }

        foreach (var candidate in pending)
        {
            var term = candidate.PreferredSpelling;
            if (IsSuppressedAutomaticTerm(term))
            {
                changed |= RemovePendingEvidence(term);
                suppressedCount++;
                continue;
            }
            var evidence = _settings.PendingDictionaryLearning.FirstOrDefault(entry =>
                string.Equals(entry.PreferredSpelling, term, StringComparison.OrdinalIgnoreCase));
            if (evidence is null)
            {
                if (_settings.PendingDictionaryLearning.Count >= 100)
                {
                    capacityCount++;
                    continue;
                }
                evidence = new DictionaryLearningEvidence { PreferredSpelling = term };
                _settings.PendingDictionaryLearning.Add(evidence);
                changed = true;
            }

            if (evidence.CountedSubmissionIds.Contains(submissionId, StringComparer.Ordinal))
            {
                duplicateCount++;
                continue;
            }

            var existing = _settings.CorrectionDictionary.FirstOrDefault(entry =>
                string.Equals(entry.PreferredSpelling, term, StringComparison.OrdinalIgnoreCase));
            if (evidence.IndependentCorrectionCount >= RequiredIndependentDictionaryCorrections - 1 &&
                existing is null && _settings.CorrectionDictionary.Count >= 100)
            {
                capacityCount++;
                continue;
            }

            evidence.IndependentCorrectionCount++;
            evidence.LastCorrectionUtc = DateTimeOffset.UtcNow;
            evidence.Alternatives = MergeDictionaryAlternatives(
                evidence.Alternatives, candidate.MisrecognizedVariants, term);
            evidence.CountedSubmissionIds.Add(submissionId);
            if (evidence.CountedSubmissionIds.Count > RequiredIndependentDictionaryCorrections)
                evidence.CountedSubmissionIds.RemoveAt(0);
            changed = true;
            pendingUpdatedCount++;

            if (evidence.IndependentCorrectionCount >= RequiredIndependentDictionaryCorrections)
            {
                _settings.PendingDictionaryLearning.Remove(evidence);
                existing = _settings.CorrectionDictionary.FirstOrDefault(entry =>
                    string.Equals(entry.PreferredSpelling, term, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var merged = MergeDictionaryAlternatives(
                        existing.Alternatives, ParseDictionaryAlternatives(evidence.Alternatives), term);
                    if (!string.Equals(existing.Alternatives, merged, StringComparison.Ordinal))
                    {
                        existing.Alternatives = merged;
                        addedCount++;
                    }
                }
                else
                {
                    _settings.CorrectionDictionary.Add(new CorrectionDictionaryEntry
                    {
                        PreferredSpelling = term,
                        Alternatives = MergeDictionaryAlternatives(
                            string.Empty, ParseDictionaryAlternatives(evidence.Alternatives), term),
                        Source = "automatic"
                    });
                    addedCount++;
                }
            }
            else
            {
                var remaining = RequiredIndependentDictionaryCorrections - evidence.IndependentCorrectionCount;
                remainingConfirmations = remainingConfirmations == 0
                    ? remaining
                    : Math.Min(remainingConfirmations, remaining);
            }
        }

        if (!changed)
            return new AutomaticDictionaryLearningResult(0, 0, 0, suppressedCount, duplicateCount,
                capacityCount, _settings.PendingDictionaryLearning.Count, false);

        try
        {
            _settingsStore.Save(_settings);
            RefreshCorrectionDictionaryItems();
            if (addedCount > 0)
                DictionaryLearningDiagnosticLog.Write("dictionary_saved", "success", addedCount, monitorId, candidateId);
            return new AutomaticDictionaryLearningResult(addedCount, pendingUpdatedCount, remainingConfirmations,
                suppressedCount, duplicateCount, capacityCount, _settings.PendingDictionaryLearning.Count, false);
        }
        catch (Exception ex)
        {
            _settings.CorrectionDictionary = previousDictionary;
            _settings.PendingDictionaryLearning = previousEvidence;
            _settings.SuppressedAutomaticDictionaryTerms = previousSuppressedTerms;
            RefreshCorrectionDictionaryItems();
            DictionaryLearningDiagnosticLog.Write("dictionary_save_failed", ex.GetType().Name,
                addedCount + pendingUpdatedCount, monitorId, candidateId);
            return new AutomaticDictionaryLearningResult(0, 0, 0, 0, 0, 0,
                _settings.PendingDictionaryLearning.Count, true);
        }

        bool RemovePendingEvidence(string preferredSpelling)
        {
            var matches = _settings.PendingDictionaryLearning.Where(entry =>
                string.Equals(entry.PreferredSpelling, preferredSpelling, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var match in matches) _settings.PendingDictionaryLearning.Remove(match);
            return matches.Length > 0;
        }
    }

    private static IReadOnlyList<DictionaryLearningCandidate> PrepareDictionaryLearningCandidates(
        IEnumerable<DictionaryTermChange> changes) => changes
        .Select(change => new
        {
            Previous = CleanDictionaryField(change.PreviousText, 100),
            Preferred = CleanDictionaryField(change.CorrectSpelling, 100)
        })
        .Where(change => change.Previous.Length >= 2 && change.Previous.Any(char.IsLetterOrDigit) &&
                         change.Preferred.Length >= 2 && change.Preferred.Any(char.IsLetterOrDigit) &&
                         !string.Equals(change.Previous, change.Preferred, StringComparison.OrdinalIgnoreCase))
        .GroupBy(change => change.Preferred, StringComparer.OrdinalIgnoreCase)
        .Select(group => new DictionaryLearningCandidate(
            group.First().Preferred,
            group.Select(change => change.Previous)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()))
        .ToArray();

    private bool IsSuppressedAutomaticTerm(string term) =>
        _settings.SuppressedAutomaticDictionaryTerms.Any(suppressed =>
            string.Equals(suppressed, term, StringComparison.OrdinalIgnoreCase));

    private void RememberSuppressedAutomaticTerm(string term)
    {
        _settings.SuppressedAutomaticDictionaryTerms.RemoveAll(suppressed =>
            string.Equals(suppressed, term, StringComparison.OrdinalIgnoreCase));
        if (_settings.SuppressedAutomaticDictionaryTerms.Count >= 1_000)
            _settings.SuppressedAutomaticDictionaryTerms.RemoveAt(0);
        _settings.SuppressedAutomaticDictionaryTerms.Add(term);
    }

    private List<DictionaryLearningEvidence> CloneDictionaryLearningEvidence() =>
        _settings.PendingDictionaryLearning.Select(entry => new DictionaryLearningEvidence
        {
            PreferredSpelling = entry.PreferredSpelling,
            Alternatives = entry.Alternatives,
            IndependentCorrectionCount = entry.IndependentCorrectionCount,
            LastCorrectionUtc = entry.LastCorrectionUtc,
            CountedSubmissionIds = entry.CountedSubmissionIds.ToList()
        }).ToList();

    private void RecordDictionaryTermUsage(IReadOnlyList<CorrectionDictionaryEntry> entries)
    {
        if (entries.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var updated = 0;
        foreach (var selected in entries)
        {
            var existing = _settings.CorrectionDictionary.FirstOrDefault(entry =>
                string.Equals(entry.PreferredSpelling, selected.PreferredSpelling, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            existing.UseCount = Math.Min(1_000_000, Math.Max(0, existing.UseCount) + 1);
            existing.LastUsedUtc = now;
            updated++;
        }
        if (updated == 0) return;
        try
        {
            _settingsStore.Save(_settings);
            DictionaryLearningDiagnosticLog.Write("dictionary_use_recorded", "success", updated);
        }
        catch (Exception ex)
        {
            DictionaryLearningDiagnosticLog.Write("dictionary_use_recorded", "save-failed-" + ex.GetType().Name, updated);
        }
    }

    private void AddCorrectionDictionaryEntry_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording) return;

        var preferred = CleanDictionaryField(DictionaryPreferredSpellingBox.Text, 100);
        if (preferred.Length == 0)
        {
            CorrectionDictionaryStatus.Text = "正しい表記を入力してください。";
            return;
        }

        var existing = _settings.CorrectionDictionary.FirstOrDefault(entry =>
            string.Equals(entry.PreferredSpelling, preferred, StringComparison.OrdinalIgnoreCase));
        var alternatives = MergeDictionaryAlternatives(
            existing?.Alternatives, ParseDictionaryAlternatives(DictionaryAlternativesBox.Text), preferred);
        if (existing is null && _settings.CorrectionDictionary.Count >= 100)
        {
            CorrectionDictionaryStatus.Text = "登録できる単語は100件までです。";
            return;
        }

        var previous = CloneCorrectionDictionary();
        var previousPending = CloneDictionaryLearningEvidence();
        var previousSuppressed = _settings.SuppressedAutomaticDictionaryTerms.ToList();
        var isNew = existing is null;
        var wasChanged = isNew || existing is not null &&
            (!string.Equals(existing.PreferredSpelling, preferred, StringComparison.Ordinal) ||
             existing.Source != "manual" || !string.Equals(existing.Alternatives, alternatives, StringComparison.Ordinal)) ||
            _settings.SuppressedAutomaticDictionaryTerms.Contains(preferred, StringComparer.OrdinalIgnoreCase);
        if (!wasChanged)
        {
            CorrectionDictionaryStatus.Text = "この単語と誤認識例はすでに登録されています。";
            return;
        }

        if (existing is null)
        {
            _settings.CorrectionDictionary.Add(new CorrectionDictionaryEntry
            {
                PreferredSpelling = preferred,
                Alternatives = alternatives,
                Source = "manual"
            });
        }
        else
        {
            existing.PreferredSpelling = preferred;
            existing.Alternatives = alternatives;
            existing.Source = "manual";
        }

        _settings.SuppressedAutomaticDictionaryTerms.RemoveAll(term =>
            string.Equals(term, preferred, StringComparison.OrdinalIgnoreCase));
        _settings.PendingDictionaryLearning.RemoveAll(entry =>
            string.Equals(entry.PreferredSpelling, preferred, StringComparison.OrdinalIgnoreCase));
        if (!PersistCorrectionDictionary(previous, isNew ? "辞書に登録しました。" : "辞書を更新しました。"))
        {
            _settings.PendingDictionaryLearning = previousPending;
            _settings.SuppressedAutomaticDictionaryTerms = previousSuppressed;
            return;
        }
        ShowDictionarySavedIndicator();
        DictionaryPreferredSpellingBox.Clear();
        DictionaryAlternativesBox.Clear();
    }

    private void RemoveCorrectionDictionaryEntry_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording || sender is not System.Windows.Controls.Button { Tag: CorrectionDictionaryEntry entry })
            return;

        var index = _settings.CorrectionDictionary.IndexOf(entry);
        if (index < 0) return;
        var previous = CloneCorrectionDictionary();
        var previousPending = CloneDictionaryLearningEvidence();
        var previousSuppressed = _settings.SuppressedAutomaticDictionaryTerms.ToList();
        _settings.CorrectionDictionary.RemoveAt(index);
        RememberSuppressedAutomaticTerm(entry.PreferredSpelling);
        _settings.PendingDictionaryLearning.RemoveAll(pending =>
            string.Equals(pending.PreferredSpelling, entry.PreferredSpelling, StringComparison.OrdinalIgnoreCase));
        if (!PersistCorrectionDictionary(previous, "辞書から削除しました。"))
        {
            _settings.PendingDictionaryLearning = previousPending;
            _settings.SuppressedAutomaticDictionaryTerms = previousSuppressed;
        }
    }

    private bool PersistCorrectionDictionary(List<CorrectionDictionaryEntry> previous, string successMessage)
    {
        try
        {
            _settingsStore.Save(_settings);
            RefreshCorrectionDictionaryItems();
            CorrectionDictionaryStatus.Text = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            _settings.CorrectionDictionary = previous;
            RefreshCorrectionDictionaryItems();
            CorrectionDictionaryStatus.Text = "辞書を保存できませんでした。 " + ex.Message;
            return false;
        }
    }

    private void ShowDictionarySavedIndicator() =>
        _indicator.ShowStatus("辞書に登録しました", TimeSpan.FromSeconds(1.5));

    private List<CorrectionDictionaryEntry> CloneCorrectionDictionary() => _settings.CorrectionDictionary
        .Select(entry => new CorrectionDictionaryEntry
        {
            PreferredSpelling = entry.PreferredSpelling,
            Alternatives = entry.Alternatives,
            Source = entry.Source,
            UseCount = entry.UseCount,
            LastUsedUtc = entry.LastUsedUtc
        })
        .ToList();

    private static string CleanDictionaryField(string? value, int maxLength)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string CleanDictionaryAlternatives(string? value, string preferredSpelling) =>
        MergeDictionaryAlternatives(string.Empty, ParseDictionaryAlternatives(value), preferredSpelling);

    private static string MergeDictionaryAlternatives(
        string? current,
        IEnumerable<string> additions,
        string preferredSpelling)
    {
        var variants = ParseDictionaryAlternatives(current)
            .Concat(additions.Select(value => CleanDictionaryField(value, 100)))
            .Where(value => value.Length >= 2 && value.Any(char.IsLetterOrDigit) &&
                            !string.Equals(value, preferredSpelling, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var result = new List<string>();
        var length = 0;
        foreach (var variant in variants)
        {
            var additionLength = variant.Length + (result.Count > 0 ? 1 : 0);
            if (length + additionLength > 400) break;
            result.Add(variant);
            length += additionLength;
        }
        return string.Join("、", result);
    }

    private static IReadOnlyList<string> ParseDictionaryAlternatives(string? value) => (value ?? string.Empty)
        .Split(['、', ',', '，', ';', '；', '\n', '\r', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => CleanDictionaryField(item, 100))
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToArray();

    private OpenCodeModelOption? FindSelectedOpenCodeModel()
    {
        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelId)) return null;

        var selected = _openCodeModels.FirstOrDefault(option =>
            string.Equals(option.ProviderId, _settings.OpenCodeModelProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ModelId, _settings.OpenCodeModelId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;

        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelProviderId))
        {
            var matches = _openCodeModels.Where(option =>
                string.Equals(option.ModelId, _settings.OpenCodeModelId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
        }

        if (!string.IsNullOrWhiteSpace(_settings.OpenCodeModelProviderId))
            return new OpenCodeModelOption(_settings.OpenCodeModelProviderId,
                _settings.OpenCodeModelProviderId, _settings.OpenCodeModelId, _settings.OpenCodeModelId);

        return null;
    }

    private OpenCodeModelOption? GetSelectedOpenCodeModel() => OpenCodeModelCombo.SelectedItem as OpenCodeModelOption;

    private IReadOnlyList<OpenCodeModelOption> GetOpenCodeModelItems()
    {
        var items = _openCodeModels.ToList();
        var selected = FindSelectedOpenCodeModel();
        if (selected is not null && !items.Any(option =>
                string.Equals(option.ProviderId, selected.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.ModelId, selected.ModelId, StringComparison.OrdinalIgnoreCase)))
            items.Insert(0, selected);
        return items;
    }

    private bool IsSelectedOpenCodeModelConnected(OpenCodeModelOption? selected) => selected is not null &&
        _openCodeModels.Any(option =>
            string.Equals(option.ProviderId, selected.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ModelId, selected.ModelId, StringComparison.OrdinalIgnoreCase));

    private void ApplyOpenCodeModels()
    {
        _loadingOpenCodeModelUi = true;
        try
        {
            var selected = FindSelectedOpenCodeModel();
            OpenCodeModelCombo.ItemsSource = GetOpenCodeModelItems();
            OpenCodeModelCombo.SelectedItem = selected;
            if (selected is not null && IsSelectedOpenCodeModelConnected(selected))
            {
                var changed = _settings.OpenCodeModelProviderId != selected.ProviderId ||
                              _settings.OpenCodeModelId != selected.ModelId;
                SaveOpenCodeModelSelection(selected);
                if (changed) _settingsStore.Save(_settings);
            }
        }
        finally { _loadingOpenCodeModelUi = false; }
    }

    private void SaveOpenCodeModelSelection(OpenCodeModelOption selection)
    {
        _settings.OpenCodeModelProviderId = selection.ProviderId;
        _settings.OpenCodeModelId = selection.ModelId;
    }

    private async Task LoadOpenCodeModelsAsync()
    {
        _openCodeModels = await _openCode.GetModelsAsync(_lifetime.Token);
        ApplyOpenCodeModels();
    }

    private async void OpenCodeModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOpenCodeModelUi) return;
        if (_isBusy || IsAnyRecording)
        {
            _loadingOpenCodeModelUi = true;
            OpenCodeModelCombo.SelectedItem = FindSelectedOpenCodeModel();
            _loadingOpenCodeModelUi = false;
            RuntimeStatus.Text = "モデルを変更するには、録音や処理を終えてください。";
            return;
        }

        if (GetSelectedOpenCodeModel() is not OpenCodeModelOption selected) return;
        var oldProviderId = _settings.OpenCodeModelProviderId;
        var oldModelId = _settings.OpenCodeModelId;
        if (string.Equals(oldProviderId, selected.ProviderId, StringComparison.Ordinal) &&
            string.Equals(oldModelId, selected.ModelId, StringComparison.Ordinal) && _openCodeReady)
            return;

        var wasReady = _openCodeReady;
        _isBusy = true;
        _openCodeReady = false;
        UpdateAiModeDisplay();
        RuntimeStatus.Text = "選択したOpenCodeモデルを準備しています…";
        try
        {
            await EnsureOpenCodeReadyAsync(_lifetime.Token);
            await _openCode.EnsureModelAvailableAsync(selected.ProviderId, selected.ModelId, _lifetime.Token);
            SaveOpenCodeModelSelection(selected);
            _settingsStore.Save(_settings);
            _openCodeReady = true;
            UpdateAiModeDisplay();
            OpenCodeModelStatus.Text = string.Empty;
            RuntimeStatus.Text = "OpenCodeモデルを選択しました。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _settings.OpenCodeModelProviderId = oldProviderId;
            _settings.OpenCodeModelId = oldModelId;
            _openCodeReady = wasReady && _openCode.IsRunning;
            RestoreOpenCodeModelSelection();
        }
        catch (Exception ex)
        {
            _settings.OpenCodeModelProviderId = oldProviderId;
            _settings.OpenCodeModelId = oldModelId;
            _openCodeReady = wasReady && _openCode.IsRunning;
            RestoreOpenCodeModelSelection();
            RuntimeStatus.Text = "OpenCodeモデルを切り替えられませんでした。 " + ex.GetBaseException().Message;
        }
        finally
        {
            _isBusy = false;
            UpdateAiModeDisplay();
        }
    }

    private async void RefreshOpenCodeModels_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || IsAnyRecording)
        {
            OpenCodeModelStatus.Text = "録音や処理を終えてからモデル一覧を更新してください。";
            return;
        }

        var wasReady = _openCodeReady;
        _isRefreshingOpenCodeModels = true;
        _isBusy = true;
        UpdateAiModeDisplay();
        OpenCodeModelStatus.Text = "OpenCode Desktopで接続中のproviderを確認しています…";
        try
        {
            await EnsureOpenCodeReadyAsync(_lifetime.Token);
            await LoadOpenCodeModelsAsync();
            var selected = GetSelectedOpenCodeModel();
            if (_openCodeModels.Count == 0)
            {
                _openCodeReady = false;
                OpenCodeModelStatus.Text = "接続済みモデルが見つかりません。OpenCode Desktopでproviderに接続してから更新してください。";
            }
            else if (!IsSelectedOpenCodeModelConnected(selected) || selected is null)
            {
                _openCodeReady = false;
                OpenCodeModelStatus.Text = $"接続済みproviderのモデルを{_openCodeModels.Count}件読み込みました。使うモデルを選択してください。";
            }
            else
            {
                await _openCode.EnsureModelAvailableAsync(selected.ProviderId, selected.ModelId, _lifetime.Token);
                SaveOpenCodeModelSelection(selected);
                _settingsStore.Save(_settings);
                _openCodeReady = true;
                OpenCodeModelStatus.Text = $"接続済みproviderのモデルを{_openCodeModels.Count}件読み込み、選択モデルの準備が完了しました。";
            }
        }
        catch (Exception ex)
        {
            _openCodeReady = wasReady && _openCode.IsRunning;
            OpenCodeModelStatus.Text = "モデル一覧を読み込めませんでした。 " + ex.Message;
        }
        finally
        {
            _isRefreshingOpenCodeModels = false;
            _isBusy = false;
            UpdateAiModeDisplay();
        }
    }

    private void RestoreOpenCodeModelSelection()
    {
        _loadingOpenCodeModelUi = true;
        OpenCodeModelCombo.SelectedItem = FindSelectedOpenCodeModel();
        _loadingOpenCodeModelUi = false;
    }

    private async Task LoadRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = await Task.Run(() => AsrService.Load(AppPaths.ModelFile, AppPaths.TokensFile), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _asr?.Dispose();
        _asr = recognizer;
        RegisterConfiguredHotkey();
        TitleText.Visibility = System.Windows.Visibility.Collapsed;
        BodyText.Text = "入力欄で押すと音声入力を始めます。録音中にSpaceを長押しすると、改行・プロンプト整形・そのまま送信の音声コマンドを使えます。編集できる文章を選択中なら音声で編集し、通常の文章を選択中ならその文章について質問できます。";
        CapacityText.Visibility = System.Windows.Visibility.Collapsed;
        ShortcutSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        MicrophoneSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        DiscordMuteSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        OpenCodeModelSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        CorrectionDictionarySettingsPanel.Visibility = System.Windows.Visibility.Visible;
        SelectedTextAnswerSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        ShortcutCaptureStatus.Text = string.Empty;
        UpdateDiscordMuteShortcutDisplay();
        UpdateDiscordMuteControlsEnabled();
        UpdateShortcutDisplay();
        UpdateAiModeDisplay();
        ActionButton.Visibility = System.Windows.Visibility.Collapsed;
        RuntimeStatus.Text = _hotkeyRegistered ? string.Empty : "ショートカットが登録されていません。別のキーを設定してください。";
    }

    private async Task RecordButton_Click()
    {
        if (_selectedTextQuestionSessionActive)
        {
            await StopSelectedTextQuestionSessionAsync();
            return;
        }

        if (_gptLiveQuestionActive)
        {
            await StopGptLiveSelectedTextQuestionAsync();
            return;
        }

        if (_selectionEditSessionActive)
        {
            await StopSelectionEditSessionAsync();
            return;
        }

        if (_isBusy || _asr is null)
        {
            RuntimeStatus.Text = _asr is null ? "先にSetupを完了してください。" : "処理が終わるまでお待ちください。";
            return;
        }

        if (!_recorder.IsRecording)
        {
            try
            {
                _pasteTarget = PasteService.GetForegroundWindowHandle();
                _recordingPrecedingTextContext = null;
                if (SelectionEditService.TryCapture(out var selection, out var selectionError))
                {
                    var selectedTextContext = selection!;
                    if (selectedTextContext.IsEditable)
                    {
                        if (!_openCodeReady)
                        {
                            RuntimeStatus.Text = "OpenCodeモデルの準備が完了していません。設定画面でモデルを選び直すか、一覧を更新してください。";
                            _indicator.ShowStatus("AI準備未完了", TimeSpan.FromSeconds(1));
                            return;
                        }

                        BeginSelectionEditSession(GetReusableUndoContext(selectedTextContext));
                        return;
                    }

                    if (_settings.SelectedTextAnswerMode == "gpt-live")
                    {
                        BeginGptLiveSelectedTextQuestion(selectedTextContext);
                        return;
                    }

                    BeginSelectedTextQuestionRecording(selectedTextContext);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(selectionError))
                {
                    RuntimeStatus.Text = selectionError;
                    _indicator.ShowStatus("選択を確認", TimeSpan.FromSeconds(1));
                    return;
                }

                if (!_openCodeReady)
                {
                    RuntimeStatus.Text = "OpenCodeモデルの準備が完了していません。設定画面でモデルを選び直すか、一覧を更新してください。";
                    _indicator.ShowStatus("AI準備未完了", TimeSpan.FromSeconds(1));
                    return;
                }

                _recordingPrecedingTextContext = PrecedingTextContextService.Capture(_pasteTarget);

                var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
                StartRecorderWithDiscordMute(_recorder.Start, selectedInput);
                _normalAudioInputActive = true;
                _pendingRawTranscriptFallback = null;
                _rawTranscriptFallbackRequested = false;
                if (!_hotkey.SetEscapeMonitoring(true))
                    RuntimeStatus.Text = "録音中です。Escapeキャンセルを登録できませんでした。";
                var voiceCommandHookReady = _hotkey.SetVoiceCommandPushToTalkEnabled(true);
                StartTranscriptPreview();
                _activationSoundPlayer.Play();
                ActionButton.Content = "録音を停止して変換";
                RuntimeStatus.Text = voiceCommandHookReady
                    ? "録音中 · Space長押しで音声コマンド · ショートカットで停止"
                    : "録音中 · Space音声コマンドを開始できませんでした · ショートカットで停止";
                _indicator.ShowStatus("録音中");
                UpdateAiModeDisplay();
            }
            catch (Exception ex)
            {
                if (!_recorder.IsRecording)
                {
                    _normalAudioInputActive = false;
                    _hotkey.SetVoiceCommandPushToTalkEnabled(false);
                    EndVoiceCommandCaptureIfActive();
                    _hotkey.SetEscapeMonitoring(false);
                    _recordingPrecedingTextContext = null;
                }
                if (!_recorder.IsRecording && !_continuousRecorder.IsRecording)
                    ReleaseDiscordMuteForAudioSession();
                RuntimeStatus.Text = ex.Message;
                if (ex.Message.Contains("Discord", StringComparison.Ordinal))
                    _indicator.ShowStatus("Discordキーを確認", TimeSpan.FromSeconds(1));
            }
            return;
        }

        await StopAndProcessAsync();
    }

    private void BeginSelectedTextQuestionRecording(SelectionEditContext context)
    {
        var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            StartRecorderWithDiscordMute(_continuousRecorder.Start, selectedInput);
            _selectedTextQuestionCancellation = cancellation;
            _selectedTextQuestionHistory.Clear();
            _selectedTextQuestionSessionActive = true;
            _selectedTextQuestionSessionStopping = false;
            _answerWindow.ResetAnswer();
            _activationSoundPlayer.Play();
            ActionButton.Content = "質問セッションを終了";
            RuntimeStatus.Text = "選択した文章について話してください。発話の区切りを検出すると回答します。ショートカットで終了できます。";
            _indicator.ShowStatus("質問できます");
            UpdateAiModeDisplay();
            _ = ProcessSelectedTextQuestionSessionAsync(context, cancellation);
        }
        catch
        {
            if (ReferenceEquals(_selectedTextQuestionCancellation, cancellation))
                _selectedTextQuestionCancellation = null;
            _selectedTextQuestionSessionActive = false;
            _selectedTextQuestionSessionStopping = false;
            ReleaseDiscordMuteForAudioSession();
            cancellation.Dispose();
            throw;
        }
    }

    private void BeginGptLiveSelectedTextQuestion(SelectionEditContext context)
    {
        if (!_gptLiveAuth.IsSignedIn)
        {
            RuntimeStatus.Text = "GPT Liveを使うには設定からChatGPTへサインインしてください。";
            _indicator.ShowStatus("サインインが必要", TimeSpan.FromSeconds(1));
            return;
        }

        var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
        var microphoneLabel = selectedInput is null || selectedInput.Id == AudioInputDeviceOption.SystemDefaultId
            ? "system-default"
            : selectedInput.DisplayName;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            AcquireDiscordMuteForAudioSession();
            var session = new GptLiveQuestionSession(_gptLiveAuth);
            _selectedTextQuestionCancellation = cancellation;
            _gptLiveQuestionSession = session;
            _gptLiveQuestionActive = true;
            _activationSoundPlayer.Play();
            _answerWindow.ResetAnswer();
            RuntimeStatus.Text = "GPT Liveの音声会話を準備しています…";
            _indicator.ShowStatus("GPT Live接続中");
            UpdateAiModeDisplay();
            _ = RunGptLiveSelectedTextQuestionAsync(context, microphoneLabel, session, cancellation);
        }
        catch
        {
            _selectedTextQuestionCancellation = null;
            _gptLiveQuestionSession = null;
            _gptLiveQuestionActive = false;
            cancellation.Dispose();
            ReleaseDiscordMuteForAudioSession();
            throw;
        }
    }

    private async Task RunGptLiveSelectedTextQuestionAsync(
        SelectionEditContext context,
        string microphoneLabel,
        GptLiveQuestionSession session,
        CancellationTokenSource cancellation)
    {
        try
        {
            await session.RunAsync(
                context.CurrentText,
                microphoneLabel,
                message => DispatchQuestionUi(() =>
                {
                    if (_closing) return;
                    _indicator.ShowStatus(GetGptLiveIndicatorText(message));
                    RuntimeStatus.Text = message;
                }),
                _ => { },
                answer =>
                {
                    if (!_closing) _answerWindow.SetCurrentAssistantText(answer);
                },
                finalAnswer =>
                {
                    if (!_closing) _answerWindow.SetFinalAssistantText(finalAnswer);
                },
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                var message = ex.GetBaseException().Message;
                RuntimeStatus.Text = "GPT Liveに接続できませんでした。 " + message;
                _indicator.ShowStatus("Live接続失敗", TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            try { await session.DisposeAsync(); } catch { }
            ReleaseDiscordMuteForAudioSession();
            if (ReferenceEquals(_gptLiveQuestionSession, session)) _gptLiveQuestionSession = null;
            if (ReferenceEquals(_selectedTextQuestionCancellation, cancellation)) _selectedTextQuestionCancellation = null;
            _gptLiveQuestionActive = false;
            if (!_closing)
            {
                _answerWindow.ResetAnswer();
                _indicator.HideStatus();
                UpdateAiModeDisplay();
            }
            cancellation.Dispose();
        }
    }

    private async Task StopGptLiveSelectedTextQuestionAsync()
    {
        _answerWindow.ResetAnswer();
        if (_gptLiveQuestionSession is { } session)
        {
            _indicator.ShowStatus("会話を終了中");
            await session.StopAsync();
        }
        else
        {
            _selectedTextQuestionCancellation?.Cancel();
        }
    }

    private async Task StopSelectedTextQuestionSessionAsync()
    {
        if (!_selectedTextQuestionSessionActive && !_continuousRecorder.IsRecording) return;

        _answerWindow.ResetAnswer();
        _selectedTextQuestionSessionStopping = true;
        _indicator.ShowStatus("質問を終了中");
        _selectedTextQuestionCancellation?.Cancel();
        try { await _continuousRecorder.StopAsync(CancellationToken.None); }
        catch { }
        _continuousRecorder.DiscardPendingUtterances();
    }

    private static string GetGptLiveIndicatorText(string message)
    {
        if (message.Contains("失敗", StringComparison.Ordinal) || message.Contains("できません", StringComparison.Ordinal))
            return "Live接続失敗";
        if (message.Contains("質問を認識", StringComparison.Ordinal)) return "質問認識中";
        if (message.Contains("回答", StringComparison.Ordinal)) return "回答中";
        if (message.Contains("質問できます", StringComparison.Ordinal)) return "質問できます";
        if (message.Contains("接続", StringComparison.Ordinal) || message.Contains("セッション", StringComparison.Ordinal))
            return "GPT Live接続中";
        if (message.Contains("準備", StringComparison.Ordinal)) return "音声準備中";
        return "GPT Live";
    }

    private void DispatchQuestionUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(action);
    }

    private async Task CancelAudioProcessingAsync()
    {
        if (_selectedTextQuestionSessionActive)
        {
            await StopSelectedTextQuestionSessionAsync();
            return;
        }

        if (_selectionEditSessionActive)
        {
            _selectionEditSessionStopping = true;
            _indicator.HideStatus();
            _selectionEditCancellation?.Cancel();
            try { await _continuousRecorder.StopAsync(CancellationToken.None); }
            catch { }
            _continuousRecorder.DiscardPendingUtterances();
            return;
        }

        if (_audioProcessingCancellation is not { IsCancellationRequested: false } audioCancellation)
            return;

        _indicator.ShowStatus("キャンセル中");
        audioCancellation.Cancel();
    }

    private bool HandleNormalAudioProcessingShortcut()
    {
        if (!_normalAudioInputActive)
            return false;

        if (string.IsNullOrWhiteSpace(_pendingRawTranscriptFallback))
        {
            RuntimeStatus.Text = "文字起こしが終わるまでお待ちください。";
            _indicator.ShowStatus("認識中");
            return true;
        }

        if (_audioProcessingCancellation is not { IsCancellationRequested: false } cancellation)
            return true;

        _rawTranscriptFallbackRequested = true;
        RuntimeStatus.Text = "AI整形を止めて、文字起こしを入力欄へ反映しています…";
        _indicator.ShowStatus("文字起こしを反映");
        cancellation.Cancel();
        return true;
    }

    private async Task HandleEscapeCancellationAsync()
    {
        try
        {
            if (!_normalAudioInputActive)
                return;

            if (_audioProcessingCancellation is { } processingCancellation)
            {
                _rawTranscriptFallbackRequested = false;
                if (!processingCancellation.IsCancellationRequested)
                    await CancelAudioProcessingAsync();
                else
                    _indicator.ShowStatus("キャンセルしました", TimeSpan.FromSeconds(1));
                return;
            }

            if (!_recorder.IsRecording)
                return;

            _normalAudioInputActive = false;
            _pendingRawTranscriptFallback = null;
            _hotkey.SetVoiceCommandPushToTalkEnabled(false);
            EndVoiceCommandCaptureIfActive();
            _hotkey.SetEscapeMonitoring(false);
            CancelTranscriptPreview();
            float[]? samples = null;
            try { samples = await _recorder.StopAsync(CancellationToken.None); }
            catch { }
            await StopTranscriptPreviewAsync();
            if (samples is not null) Array.Clear(samples);
            _recordingPrecedingTextContext = null;
            ReleaseDiscordMuteForAudioSession();
            if (!_closing)
            {
                _transcriptPreviewWindow.HidePreview();
                ActionButton.Content = "録音を開始";
                RuntimeStatus.Text = "音声入力をキャンセルしました。";
                _indicator.ShowStatus("キャンセルしました", TimeSpan.FromSeconds(1));
                UpdateAiModeDisplay();
            }
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                RuntimeStatus.Text = "Escapeでのキャンセルに失敗しました。 " + ex.Message;
                _indicator.ShowStatus("キャンセル失敗", TimeSpan.FromSeconds(1));
            }
        }
    }

    private void BeginSelectionEditSession(SelectionEditContext context)
    {
        var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            StartRecorderWithDiscordMute(_continuousRecorder.Start, selectedInput);
            _selectionEditCancellation = cancellation;
            _selectionEditSessionActive = true;
            _selectionEditSessionStopping = false;
            _activationSoundPlayer.Play();
            RuntimeStatus.Text = "選択文を編集します。続けて話してください。ショートカットで終了できます。";
            ActionButton.Content = "編集セッションを終了";
            _indicator.ShowStatus("選択文編集中");
            UpdateAiModeDisplay();
            _ = ProcessSelectionEditSessionAsync(context, cancellation.Token);
        }
        catch
        {
            if (_selectionEditCancellation == cancellation) _selectionEditCancellation = null;
            _selectionEditSessionActive = false;
            _selectionEditSessionStopping = false;
            ReleaseDiscordMuteForAudioSession();
            cancellation.Dispose();
            throw;
        }
    }

    private void StartRecorderWithDiscordMute(Action<int> start, AudioInputDeviceOption? selectedInput)
    {
        AcquireDiscordMuteForAudioSession();
        try
        {
            StartRecorderWithFallback(start, selectedInput);
        }
        catch
        {
            ReleaseDiscordMuteForAudioSession();
            throw;
        }
    }

    private void AcquireDiscordMuteForAudioSession()
    {
        _discordMuteLease ??= _discordPushToMute.Acquire(
            _settings.DiscordAutoMuteEnabled,
            GetDiscordMuteShortcut());
    }

    private void ReleaseDiscordMuteForAudioSession()
    {
        var lease = _discordMuteLease;
        _discordMuteLease = null;
        lease?.Dispose();
    }

    private void StartRecorderWithFallback(Action<int> start, AudioInputDeviceOption? selectedInput)
    {
        try
        {
            start(selectedInput?.DeviceNumber ?? -1);
        }
        catch (Exception selectedDeviceError) when (selectedInput is { DeviceNumber: >= 0 })
        {
            try
            {
                start(-1);
                MicrophoneDeviceStatus.Text = "選択したマイクを使えないため、システム設定の既定マイクで録音します。";
            }
            catch (Exception fallbackError)
            {
                throw new InvalidOperationException(
                    "選択したマイクとシステム設定の既定マイクを開始できませんでした。入力デバイスを確認してください。",
                    new AggregateException(selectedDeviceError, fallbackError));
            }
        }
    }

    private void StartTranscriptPreview()
    {
        if (_asr is null) return;

        _transcriptPreviewWindow.HidePreview();
        var session = Interlocked.Increment(ref _transcriptPreviewSession);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _transcriptPreviewCancellation = cancellation;
        _transcriptPreviewTask = RunTranscriptPreviewAsync(session, cancellation.Token);
    }

    private async Task RunTranscriptPreviewAsync(long session, CancellationToken cancellationToken)
    {
        var segmentStates = new List<TranscriptPreviewSegmentState>();
        SileroVoiceActivityDetector? speechVad = null;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_recorder.TryGetPreviewSegmentsSnapshot(out var snapshots, out var activeSegmentIndex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var recognizer = _asr;
                    if (recognizer is null) return;

                    for (var i = 0; i < snapshots.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var segment = snapshots[i];
                        while (segmentStates.Count <= i)
                            segmentStates.Add(new TranscriptPreviewSegmentState(segment.IsVoiceCommand));

                        var state = segmentStates[i];
                        if (state.IsVoiceCommand != segment.IsVoiceCommand)
                        {
                            state = new TranscriptPreviewSegmentState(segment.IsVoiceCommand);
                            segmentStates[i] = state;
                        }

                        var isActiveSegment = i == activeSegmentIndex;
                        if (state.IsFinalized)
                            continue;

                        var minimumSamples = segment.IsVoiceCommand ? 5 * SileroVoiceActivityDetector.WindowSamples : 16_000;
                        if (segment.Samples.Length < minimumSamples)
                        {
                            if (!isActiveSegment)
                                state.IsFinalized = true;
                            continue;
                        }

                        speechVad ??= new SileroVoiceActivityDetector();
                        if (!speechVad.ContainsSustainedSpeech(segment.Samples))
                        {
                            state.Text = string.Empty;
                            if (!isActiveSegment)
                                state.IsFinalized = true;
                            continue;
                        }

                        var transcript = await Task.Run(() => recognizer.Transcribe(segment.Samples), cancellationToken)
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        transcript = TranscriptFormatter.NormalizeTranscript(transcript);
                        if (TranscriptFormatter.HasMeaningfulText(transcript))
                            state.Text = transcript;
                        if (!isActiveSegment)
                            state.IsFinalized = true;
                    }

                    var previewParts = segmentStates
                        .Select(state =>
                        {
                            var isLineBreakCommand = state.IsVoiceCommand &&
                                VoiceCommandParser.TryParseVoiceTextCommand(state.Text, out var command) &&
                                command == VoiceTextCommand.InsertNewLine;
                            return new TranscriptPreviewPart(
                                isLineBreakCommand ? string.Empty : state.Text,
                                state.IsVoiceCommand,
                                isLineBreakCommand);
                        })
                        .Where(part => part.IsLineBreakCommand ||
                            TranscriptFormatter.HasMeaningfulText(part.Text))
                        .ToArray();
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            if (session != Interlocked.Read(ref _transcriptPreviewSession) ||
                                !_recorder.IsRecording || _closing)
                                return;

                            if (previewParts.Length > 0)
                                _transcriptPreviewWindow.ShowSegmentedPreview(previewParts);
                            else
                                _transcriptPreviewWindow.HidePreview();
                        }));
                }
                finally
                {
                    foreach (var segment in snapshots)
                        Array.Clear(segment.Samples);
                }

                var activeIsVoiceCommand = activeSegmentIndex >= 0 &&
                    activeSegmentIndex < snapshots.Count && snapshots[activeSegmentIndex].IsVoiceCommand;
                var nextDelay = activeIsVoiceCommand
                    ? TimeSpan.FromMilliseconds(900)
                    : TimeSpan.FromMilliseconds(2000);
                await DelayUntilPreviewOrModeChangeAsync(nextDelay, activeIsVoiceCommand, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // Preview is best-effort. The final full-recording decode remains authoritative.
        }
        finally
        {
            speechVad?.Dispose();
        }
    }

    private async Task DelayUntilPreviewOrModeChangeAsync(
        TimeSpan delay,
        bool isVoiceCommand,
        CancellationToken cancellationToken)
    {
        var remaining = delay;
        var pollInterval = TimeSpan.FromMilliseconds(100);
        while (remaining > TimeSpan.Zero && _voiceCommandModeActive == isVoiceCommand)
        {
            var wait = remaining < pollInterval ? remaining : pollInterval;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            remaining -= wait;
        }
    }

    private sealed class TranscriptPreviewSegmentState(bool isVoiceCommand)
    {
        public bool IsVoiceCommand { get; } = isVoiceCommand;
        public string Text { get; set; } = string.Empty;
        public bool IsFinalized { get; set; }
    }

    private async Task StopTranscriptPreviewAsync()
    {
        var cancellation = _transcriptPreviewCancellation;
        var task = _transcriptPreviewTask;
        CancelTranscriptPreview();

        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch { }
        }

        if (ReferenceEquals(_transcriptPreviewCancellation, cancellation))
        {
            _transcriptPreviewCancellation = null;
            _transcriptPreviewTask = null;
            cancellation?.Dispose();
        }
    }

    private void CancelTranscriptPreview()
    {
        var cancellation = _transcriptPreviewCancellation;
        if (cancellation is null) return;
        Interlocked.Increment(ref _transcriptPreviewSession);
        cancellation.Cancel();
    }

    private async Task StopSelectionEditSessionAsync()
    {
        if (!_selectionEditSessionActive || _selectionEditSessionStopping) return;

        _selectionEditSessionStopping = true;
        RuntimeStatus.Text = "録音を終了しています。未処理の発話を順番に編集します…";
        ActionButton.Content = "編集を終了しています…";
        _indicator.ShowStatus("終了処理中");
        try
        {
            await _continuousRecorder.StopAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "連続録音を終了できませんでした。 " + ex.Message;
        }
    }

    private async Task ProcessSelectionEditSessionAsync(SelectionEditContext context, CancellationToken cancellationToken)
    {
        var sessionEndIndicator = "編集終了";
        try
        {
            await foreach (var samples in _continuousRecorder.ReadUtterancesAsync(cancellationToken))
            {
                _undoWindow.HideUndo(stopTimer: false);
                _isBusy = true;
                ActionButton.Content = "選択文を編集しています…";
                RuntimeStatus.Text = "発話を文字起こししています…";
                _indicator.ShowStatus("音声認識中");
                try
                {
                    var transcript = await Task.Run(() => _asr!.Transcribe(samples), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    transcript = TranscriptFormatter.NormalizeTranscript(transcript);
                    if (!TranscriptFormatter.HasMeaningfulText(transcript))
                    {
                        RuntimeStatus.Text = "音声を認識できませんでした。続けて話してください。";
                        _indicator.ShowStatus("選択文編集中");
                        continue;
                    }

                    if (IsUndoSelectionEditCommand(transcript))
                    {
                        _indicator.ShowStatus("元に戻しています");
                        var wasUndone = await UndoLastSelectionEditAsync(context, cancellationToken);
                        RuntimeStatus.Text = wasUndone
                            ? "直前のAI編集を元に戻しました。続けて話してください。"
                            : "直前に戻せるAI編集がありません。選択文は変更していません。";
                        _indicator.ShowStatus("選択文編集中");
                        continue;
                    }

                    _indicator.ShowStatus("AI編集中");
                    RuntimeStatus.Text = "選択文へ音声指示を適用しています…";
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var replacement = await _openCode.EditSelectedTextAsync(
                        context.CurrentText,
                        transcript,
                        _settings.OpenCodeModelProviderId,
                        _settings.OpenCodeModelId,
                        cancellationToken);

                    var previousText = context.CurrentText;
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await SelectionEditService.ReplaceCurrentSelectionAsync(
                        context, replacement, cancellationToken);
                    if (result.Applied && !string.Equals(previousText, context.CurrentText, StringComparison.Ordinal))
                    {
                        _lastSelectionEditUndo = new SelectionEditUndoRecord(
                            context, previousText, context.CurrentText);
                        _undoWindow.ShowUndo();
                    }
                    RuntimeStatus.Text = result.Message;
                    _indicator.ShowStatus(result.Applied
                            ? result.CanContinue ? "選択文編集中" : "選択維持失敗"
                            : "置換失敗",
                        result.CanContinue ? null : TimeSpan.FromSeconds(1));

                    if (!result.CanContinue)
                    {
                        sessionEndIndicator = result.Applied ? "選択維持失敗" : "置換失敗";
                        _selectionEditSessionStopping = true;
                        await _continuousRecorder.StopAsync(_lifetime.Token);
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _openCode.Stop();
                    RuntimeStatus.Text = "AI編集に失敗しました。選択文は変更していません。 " + ex.GetBaseException().Message;
                    _indicator.ShowStatus("AI編集失敗");
                }
                finally
                {
                    Array.Clear(samples);
                    _isBusy = false;
                    ActionButton.Content = _selectionEditSessionStopping ? "編集を終了しています…" : "編集セッションを終了";
                    UpdateAiModeDisplay();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "連続音声の処理を終了しました。 " + ex.GetBaseException().Message;
            sessionEndIndicator = "録音エラー";
        }
        finally
        {
            if (_continuousRecorder.IsRecording)
            {
                try { await _continuousRecorder.StopAsync(CancellationToken.None); }
                catch { }
            }
            _continuousRecorder.DiscardPendingUtterances();

            var endedDuringShutdown = _closing;
            var wasCanceled = !endedDuringShutdown && cancellationToken.IsCancellationRequested;
            _selectionEditSessionActive = false;
            _selectionEditSessionStopping = false;
            ReleaseDiscordMuteForAudioSession();
            _isBusy = false;
            if (!_undoWindow.IsUndoVisible)
            {
                _undoWindow.HideUndo();
                _lastSelectionEditUndo = null;
            }
            _selectionEditCancellation?.Dispose();
            _selectionEditCancellation = null;
            ActionButton.Content = _asr is null ? "Setup" : "録音を開始";
            if (endedDuringShutdown || wasCanceled) _indicator.HideStatus();
            else _indicator.ShowStatus(sessionEndIndicator, TimeSpan.FromSeconds(1));
            UpdateAiModeDisplay();
        }
    }

    private SelectionEditContext GetReusableUndoContext(SelectionEditContext captured)
    {
        var undo = _lastSelectionEditUndo;
        if (undo is null || !_undoWindow.IsUndoVisible)
            return captured;

        var previousContext = undo.Context;
        if (previousContext.TargetWindow != captured.TargetWindow ||
            !previousContext.IsEditable || !captured.IsEditable ||
            !string.Equals(previousContext.CurrentText, captured.CurrentText, StringComparison.Ordinal))
            return captured;

        previousContext.UpdateProvider(captured.ProviderElement);
        previousContext.UpdateLastKnownSelectionRange(captured.CloneLastKnownSelectionRange());
        return previousContext;
    }

    private async void UndoWindow_UndoRequested(object? sender, EventArgs e)
    {
        if (_closing) return;

        var context = _lastSelectionEditUndo?.Context;
        if (context is null)
        {
            RuntimeStatus.Text = "元に戻せるAI編集がありません。";
            _indicator.ShowStatus(_selectionEditSessionActive ? "選択文編集中" : "取り消せません",
                _selectionEditSessionActive ? null : TimeSpan.FromSeconds(1));
            return;
        }

        try
        {
            var wasUndone = await UndoLastSelectionEditAsync(context, _lifetime.Token);
            RuntimeStatus.Text = wasUndone
                ? "直前のAI編集を元に戻しました。"
                : "元に戻せませんでした。対象の文章が変更されたか、選択状態を確認できません。";
            _indicator.ShowStatus(_selectionEditSessionActive ? "選択文編集中" :
                wasUndone ? "元に戻しました" : "取り消せません",
                _selectionEditSessionActive ? null : TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "AI編集を元に戻せませんでした。 " + ex.GetBaseException().Message;
            _indicator.ShowStatus(_selectionEditSessionActive ? "選択文編集中" : "取り消せません",
                _selectionEditSessionActive ? null : TimeSpan.FromSeconds(1));
        }
    }

    private void UndoWindow_UndoExpired(object? sender, EventArgs e)
    {
        if (!_selectionEditSessionActive)
            _lastSelectionEditUndo = null;
    }

    private async Task<bool> UndoLastSelectionEditAsync(
        SelectionEditContext context,
        CancellationToken cancellationToken)
    {
        var undo = _lastSelectionEditUndo;
        if (undo is null)
            return false;

        if (!ReferenceEquals(undo.Context, context) ||
            !string.Equals(context.CurrentText, undo.AppliedText, StringComparison.Ordinal))
        {
            if (ReferenceEquals(_lastSelectionEditUndo, undo))
                _lastSelectionEditUndo = null;
            _undoWindow.HideUndo();
            return false;
        }

        if (!await SelectionEditService.TryReselectCurrentSelectionAsync(context, undo.AppliedText, cancellationToken))
        {
            if (ReferenceEquals(_lastSelectionEditUndo, undo))
                _lastSelectionEditUndo = null;
            _undoWindow.HideUndo();
            return false;
        }

        var result = await SelectionEditService.ReplaceCurrentSelectionAsync(
            context, undo.PreviousText, cancellationToken);
        if (!result.Applied)
        {
            if (ReferenceEquals(_lastSelectionEditUndo, undo))
                _lastSelectionEditUndo = null;
            _undoWindow.HideUndo();
            return false;
        }

        if (ReferenceEquals(_lastSelectionEditUndo, undo))
            _lastSelectionEditUndo = null;
        _undoWindow.HideUndo();
        return true;
    }

    private static bool IsUndoSelectionEditCommand(string transcript)
    {
        var compact = string.Concat(transcript.Where(character =>
            !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && !char.IsSymbol(character)));
        if (compact.Length == 0)
            return false;

        return compact.EndsWith("戻してください", StringComparison.Ordinal) ||
               compact.EndsWith("戻してほしい", StringComparison.Ordinal) ||
               compact.EndsWith("戻してくれ", StringComparison.Ordinal) ||
               compact.EndsWith("戻して", StringComparison.Ordinal) ||
               compact.EndsWith("前の文章に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("前の状態に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("一つ前に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("直前に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("元の文章に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("元に戻す", StringComparison.Ordinal) ||
               compact.EndsWith("取り消してください", StringComparison.Ordinal) ||
               compact.EndsWith("取り消してほしい", StringComparison.Ordinal) ||
               compact.EndsWith("取り消して", StringComparison.Ordinal) ||
               compact.EndsWith("編集を取り消す", StringComparison.Ordinal) ||
               compact.EndsWith("ロールバックしてください", StringComparison.Ordinal) ||
               compact.EndsWith("ロールバックして", StringComparison.Ordinal) ||
               compact.EndsWith("アンドゥ", StringComparison.Ordinal) ||
               string.Equals(compact, "undo", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessSelectedTextQuestionSessionAsync(
        SelectionEditContext selectedText,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var failed = false;
        try
        {
            await foreach (var samples in _continuousRecorder.ReadUtterancesAsync(cancellationToken))
            {
                _isBusy = true;
                UpdateAiModeDisplay();
                ActionButton.Content = "質問に回答しています…";
                RuntimeStatus.Text = "発話を文字起こししています…";
                _indicator.ShowStatus("質問認識中");
                try
                {
                    var question = await Task.Run(() => _asr!.Transcribe(samples), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    question = TranscriptFormatter.NormalizeTranscript(question);
                    if (!TranscriptFormatter.HasMeaningfulText(question))
                    {
                        RuntimeStatus.Text = "発話を認識できませんでした。続けて話してください。";
                        _indicator.ShowStatus("質問できます");
                        continue;
                    }

                    RuntimeStatus.Text = "OpenCodeを準備しています…";
                    _indicator.ShowStatus("OpenCode接続中");
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var model = GetSelectedOpenCodeModel();
                    var providerId = model?.ProviderId ?? _settings.OpenCodeModelProviderId;
                    var modelId = model?.ModelId ?? _settings.OpenCodeModelId;
                    if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
                        throw new InvalidOperationException("設定からOpenCodeモデルを選択してください。");

                    RuntimeStatus.Text = "選択した文章とこれまでの会話を参考に回答しています…";
                    _indicator.ShowStatus("回答中");
                    var answer = await _openCode.AnswerSelectedTextAsync(
                        selectedText.CurrentText,
                        question,
                        providerId,
                        modelId,
                        onTextUpdate: _answerWindow.SetCurrentAssistantText,
                        cancellationToken: cancellationToken,
                        conversationHistory: _selectedTextQuestionHistory.ToArray());
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TranscriptFormatter.HasMeaningfulText(answer))
                        throw new InvalidDataException("モデルから回答が返りませんでした。選択中の文章は変更していません。");

                    answer = answer.Trim();
                    _answerWindow.SetFinalAssistantText(answer);
                    _selectedTextQuestionHistory.Add((question, answer));
                    RuntimeStatus.Text = "回答しました。続けて質問できます。";
                    _indicator.ShowStatus("質問できます");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _answerWindow.ClearAnswer();
                    break;
                }
                catch (Exception ex)
                {
                    if (!_closing)
                    {
                        var message = ex.GetBaseException().Message;
                        RuntimeStatus.Text = "選択文への回答に失敗しました。 " + message;
                        _indicator.ShowStatus("回答失敗", TimeSpan.FromSeconds(1));
                    }
                }
                finally
                {
                    Array.Clear(samples);
                    _isBusy = false;
                    ActionButton.Content = _selectedTextQuestionSessionStopping
                        ? "質問を終了しています…"
                        : "質問セッションを終了";
                    UpdateAiModeDisplay();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failed = true;
            if (!_closing)
            {
                RuntimeStatus.Text = "連続音声の処理を終了しました。 " + ex.GetBaseException().Message;
                _indicator.ShowStatus("質問処理失敗", TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            if (_continuousRecorder.IsRecording)
            {
                try { await _continuousRecorder.StopAsync(CancellationToken.None); }
                catch { }
            }
            _continuousRecorder.DiscardPendingUtterances();

            var wasCanceled = !_closing && cancellationToken.IsCancellationRequested;
            ReleaseDiscordMuteForAudioSession();
            _selectedTextQuestionSessionActive = false;
            _selectedTextQuestionSessionStopping = false;
            _selectedTextQuestionHistory.Clear();
            _isBusy = false;
            if (ReferenceEquals(_selectedTextQuestionCancellation, cancellation))
                _selectedTextQuestionCancellation = null;
            cancellation.Dispose();
            ActionButton.Content = _asr is null ? "Setup" : "録音を開始";
            if (!_closing)
            {
                _answerWindow.ResetAnswer();
                if (failed) { }
                else if (wasCanceled) _indicator.ShowStatus("質問終了", TimeSpan.FromSeconds(1));
                else _indicator.HideStatus();
                UpdateAiModeDisplay();
            }
        }
    }

    private void BeginVoiceCommandCapture()
    {
        if (_voiceCommandModeActive)
            return;

        if (!_normalAudioInputActive || !_recorder.IsRecording)
        {
            if (_normalAudioInputActive && !_closing)
            {
                RuntimeStatus.Text = "音声コマンドを開始できませんでした。通常の音声録音が続いているか確認してください。";
                _indicator.ShowStatus("コマンド開始失敗", TimeSpan.FromSeconds(1));
            }
            return;
        }

        try
        {
            _recorder.BeginVoiceCommandCapture();
            _voiceCommandModeActive = true;
            RuntimeStatus.Text = "音声コマンド中 · Spaceを離すと通常の音声入力に戻ります。";
            _indicator.ShowStatus("音声コマンド中");
        }
        catch (Exception ex)
        {
            _hotkey.SetVoiceCommandPushToTalkEnabled(false);
            RuntimeStatus.Text = "音声コマンドを開始できませんでした。 " + ex.GetBaseException().Message;
            _indicator.ShowStatus("コマンド開始失敗", TimeSpan.FromSeconds(1));
        }
    }

    private void EndVoiceCommandCaptureIfActive()
    {
        if (!_voiceCommandModeActive)
            return;

        try { _recorder.EndVoiceCommandCapture(); }
        finally { _voiceCommandModeActive = false; }

        if (_normalAudioInputActive && _recorder.IsRecording && !_closing)
        {
            RuntimeStatus.Text = "録音中 · Space長押しで音声コマンド · ショートカットで停止";
            _indicator.ShowStatus("録音中");
        }
    }

    private async Task<NormalAudioDecodeResult> TranscribeNormalAudioSegmentsAsync(
        IReadOnlyList<AudioRecordingSegment> segments,
        CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();
        var actions = new List<(float[]? Samples, VoiceTextCommand? Command, string? EditInstruction)>();
        var editInstructions = new List<string>();
        var voiceEditCommands = new List<VoiceEditCommandInstruction>();
        var voiceCommandSessionId = Guid.NewGuid().ToString("N");
        SileroVoiceActivityDetector? speechVad = null;
        var formatAsPrompt = false;
        var sendAsIs = false;
        var hadUnrecognizedVoiceCommand = false;

        void PreserveRawTranscriptFallback()
        {
            var rawTranscript = VoiceCommandParser.RemoveVoiceCommandMarkers(transcript.ToString());
            if (TranscriptFormatter.HasMeaningfulText(rawTranscript))
                _pendingRawTranscriptFallback = rawTranscript;
        }

        try
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.IsVoiceCommand)
                {
                    if (segment.Samples.Length < 5 * SileroVoiceActivityDetector.WindowSamples)
                    {
                        hadUnrecognizedVoiceCommand = true;
                        continue;
                    }

                    speechVad ??= new SileroVoiceActivityDetector();
                    if (!speechVad.ContainsSustainedSpeech(segment.Samples))
                    {
                        hadUnrecognizedVoiceCommand = true;
                        AudioInputDiagnosticLog.Write("voice-command-vad", "rejected-no-sustained-speech");
                        continue;
                    }

                    var recognized = await Task.Run(() => _asr!.Transcribe(segment.Samples));
                    cancellationToken.ThrowIfCancellationRequested();
                    recognized = TranscriptFormatter.NormalizeTranscript(recognized);
                    if (!TranscriptFormatter.HasMeaningfulText(recognized))
                    {
                        hadUnrecognizedVoiceCommand = true;
                        continue;
                    }

                    if (!VoiceCommandParser.TryParseVoiceTextCommand(recognized, out var command))
                    {
                        actions.Add((null, null, recognized));
                        continue;
                    }
                    actions.Add((null, command, null));
                    continue;
                }

                if (segment.Samples.Length > 0)
                    actions.Add((segment.Samples, null, null));
            }

            var dictatedAudioSegments = new List<float[]>();
            async Task FlushDictatedAudioAsync()
            {
                if (dictatedAudioSegments.Count == 0)
                    return;

                var combined = CombineDictatedAudio(dictatedAudioSegments);
                dictatedAudioSegments.Clear();
                try
                {
                    if (combined.Length >= 1600)
                    {
                        speechVad ??= new SileroVoiceActivityDetector();
                        if (!speechVad.ContainsSustainedSpeech(combined))
                        {
                            AudioInputDiagnosticLog.Write("dictation-vad", "rejected-no-sustained-speech");
                            return;
                        }

                        var recognized = await Task.Run(() => _asr!.Transcribe(combined));
                        cancellationToken.ThrowIfCancellationRequested();
                        recognized = TranscriptFormatter.NormalizeTranscript(recognized);
                        if (TranscriptFormatter.HasMeaningfulText(recognized))
                        {
                            if (transcript.Length > 0 && transcript[^1] != '\n' && !char.IsWhiteSpace(transcript[^1]))
                                transcript.Append(' ');
                            transcript.Append(recognized);
                            PreserveRawTranscriptFallback();
                        }
                    }
                }
                finally
                {
                    Array.Clear(combined);
                }
            }

            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (action.EditInstruction is { } editInstruction)
                {
                    await FlushDictatedAudioAsync();
                    var marker = VoiceCommandParser.CreateVoiceCommandMarker(
                        voiceCommandSessionId, voiceEditCommands.Count);
                    if (transcript.Length > 0 && !char.IsWhiteSpace(transcript[^1]))
                        transcript.Append(' ');
                    transcript.Append(marker).Append(' ');
                    PreserveRawTranscriptFallback();
                    voiceEditCommands.Add(new VoiceEditCommandInstruction(marker, editInstruction));
                    continue;
                }

                if (action.Command is not { } command)
                {
                    if (action.Samples is { Length: > 0 } dictatedSamples)
                        dictatedAudioSegments.Add(dictatedSamples);
                    continue;
                }

                switch (command)
                {
                    case VoiceTextCommand.InsertNewLine:
                        await FlushDictatedAudioAsync();
                        while (transcript.Length > 0 && char.IsWhiteSpace(transcript[^1]))
                            transcript.Length--;
                        if (transcript.Length > 0 && transcript[^1] != '\n')
                        {
                            transcript.Append('\n');
                            PreserveRawTranscriptFallback();
                        }
                        break;
                    case VoiceTextCommand.FormatAsPrompt:
                        formatAsPrompt = true;
                        if (!editInstructions.Contains(VoiceCommandParser.PromptFormatInstruction,
                                StringComparer.Ordinal))
                            editInstructions.Add(VoiceCommandParser.PromptFormatInstruction);
                        break;
                    case VoiceTextCommand.SendAsIs:
                        await FlushDictatedAudioAsync();
                        sendAsIs = true;
                        break;
                }
            }

            await FlushDictatedAudioAsync();
            PreserveRawTranscriptFallback();

            return new NormalAudioDecodeResult(
                transcript.ToString().Trim(' ', '\t', '\r'),
                formatAsPrompt,
                sendAsIs,
                editInstructions,
                voiceEditCommands,
                hadUnrecognizedVoiceCommand);
        }
        finally
        {
            speechVad?.Dispose();
            foreach (var segment in segments)
                Array.Clear(segment.Samples);
        }
    }

    private static float[] CombineDictatedAudio(IReadOnlyList<float[]> segments)
    {
        const int InterSegmentSilenceSamples = 1600;
        var totalSamples = segments.Sum(segment => segment.Length) +
                           Math.Max(0, segments.Count - 1) * InterSegmentSilenceSamples;
        var combined = new float[totalSamples];
        var offset = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            Array.Copy(segment, 0, combined, offset, segment.Length);
            offset += segment.Length;
            if (index < segments.Count - 1)
                offset += InterSegmentSilenceSamples;
        }
        return combined;
    }

    private async Task StopAndProcessAsync()
    {
        var precedingTextContext = _recordingPrecedingTextContext;
        _recordingPrecedingTextContext = null;
        var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _audioProcessingCancellation = processingCancellation;
        var cancellationToken = processingCancellation.Token;
        _isBusy = true;
        UpdateAiModeDisplay();
        ActionButton.IsEnabled = false;
        ActionButton.Content = "音声を文字起こししています…";
        RuntimeStatus.Text = "音声をPC内で文字起こししています…";
        _indicator.ShowStatus("認識中");
        IReadOnlyList<AudioRecordingSegment>? recordingSegments = null;
        Task? sendCommandListenerTask = null;
        var sendCommandDetected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCommandMonitorUnavailable = false;
        var sendAfterPaste = false;
        var pipelineStage = "recording-stop";
        var outputAttempted = false;
        var rawFallbackUsed = false;
        var completionIndicator = "完了";
        string? aiFailureMessage = null;
        _pendingRawTranscriptFallback = null;
        try
        {
            _hotkey.SetVoiceCommandPushToTalkEnabled(false);
            EndVoiceCommandCaptureIfActive();
            CancelTranscriptPreview();
            recordingSegments = await _recorder.StopAndGetSegmentsAsync(cancellationToken);
            await StopTranscriptPreviewAsync();
            pipelineStage = "transcription";
            var decoded = await TranscribeNormalAudioSegmentsAsync(recordingSegments, cancellationToken);
            var transcript = decoded.Transcript;
            var formatAsPrompt = decoded.FormatAsPrompt;
            var sendAsIs = decoded.SendAsIs;
            var editInstructions = decoded.EditInstructions;
            var voiceEditCommands = decoded.VoiceEditCommands;
            var hasVoiceEditInstructions = editInstructions.Count > 0 || voiceEditCommands.Count > 0;
            var hasNonPromptEditInstructions = voiceEditCommands.Count > 0 || editInstructions.Any(instruction =>
                !string.Equals(instruction, VoiceCommandParser.PromptFormatInstruction, StringComparison.Ordinal));
            var usePromptStyleRewrite = formatAsPrompt && !hasNonPromptEditInstructions;
            var transcriptForPaste = VoiceCommandParser.RemoveVoiceCommandMarkers(transcript);
            if (!TranscriptFormatter.HasMeaningfulText(transcriptForPaste))
                throw new InvalidDataException("文字起こし結果が空、または句読点・記号だけでした。マイクを確認して、もう一度お試しください。");

            sendAfterPaste = decoded.SendAsIs;

            if (VoiceCommandParser.TryRemoveSendDirective(transcript, out var transcriptWithoutCommand))
            {
                sendAfterPaste = true;
                transcript = transcriptWithoutCommand;
                transcriptForPaste = VoiceCommandParser.RemoveVoiceCommandMarkers(transcript);
                if (!TranscriptFormatter.HasMeaningfulText(transcriptForPaste))
                    throw new InvalidDataException("送信コマンドの前に文章がありません。入力欄は変更していません。");
            }
            _pendingRawTranscriptFallback = transcriptForPaste;
            _transcriptPreviewWindow.ShowPreview(transcriptForPaste);
            var explicitLineBreakCount = VoiceCommandParser.CountExplicitLineBreaks(transcript);
            var transcriptForAi = VoiceCommandParser.MarkExplicitLineBreaks(transcript);

            string text;
            string resultStatus;
            _indicator.HideStatus();
            if (sendAsIs)
            {
                text = transcriptForPaste;
                resultStatus = "文字起こしをそのまま入力欄へ貼り付けました。";
            }
            else
            {
                pipelineStage = "ai-processing";
                _indicator.ShowStatus("整形中");
                RuntimeStatus.Text = usePromptStyleRewrite
                    ? "音声コマンドに従ってプロンプト風に整えています…"
                    : hasVoiceEditInstructions
                        ? "音声コマンドの編集指示を反映しています…"
                    : "OpenCodeを準備しています…";
                if (!sendAfterPaste)
                {
                    try
                    {
                        var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
                        StartRecorderWithDiscordMute(_continuousRecorder.StartForSendCommands, selectedInput);
                        sendCommandListenerTask = MonitorSendVoiceCommandsAsync(sendCommandDetected, cancellationToken);
                        _sendCommandListenerTask = sendCommandListenerTask;
                        RuntimeStatus.Text = usePromptStyleRewrite
                            ? "音声コマンドに従ってプロンプト風に整えています。送信も音声で指示できます。"
                            : hasVoiceEditInstructions
                                ? "音声コマンドの編集指示を反映しています。送信も音声で指示できます。"
                            : "OpenCodeの選択モデルで文章を整えています。音声で送信を指示できます。";
                    }
                    catch
                    {
                        sendCommandMonitorUnavailable = true;
                    }
                }

                try
                {
                    if (sendCommandListenerTask is null && !sendAfterPaste && !hasVoiceEditInstructions)
                        RuntimeStatus.Text = "OpenCodeの選択モデルで文章を整えています…";
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync(cancellationToken);
                    var relevantDictionary = DictionaryTermSelector.SelectRelevant(
                        transcriptForPaste, _settings.CorrectionDictionary);
                    var response = usePromptStyleRewrite
                        ? await _openCode.FormatTranscriptAsPromptAsync(
                            transcriptForAi, _settings.OpenCodeModelProviderId, _settings.OpenCodeModelId,
                            relevantDictionary, cancellationToken, precedingTextContext)
                        : await _openCode.RewriteAsync(
                            transcriptForAi, _settings.OpenCodeModelProviderId, _settings.OpenCodeModelId,
                            relevantDictionary, cancellationToken, precedingTextContext, editInstructions,
                            voiceEditCommands);
                    cancellationToken.ThrowIfCancellationRequested();
                    response = VoiceCommandParser.RemoveEchoedVoiceCommandText(
                        response, transcript, voiceEditCommands, out var removedCommandEcho);
                    if (removedCommandEcho)
                        AudioInputDiagnosticLog.Write("voice-command-output", "removed-echoed-instruction");
                    if (sendCommandListenerTask is not null)
                    {
                        await StopSendCommandMonitorAsync(sendCommandListenerTask);
                        if (ReferenceEquals(_sendCommandListenerTask, sendCommandListenerTask))
                            _sendCommandListenerTask = null;
                        sendCommandListenerTask = null;
                        sendAfterPaste = sendCommandDetected.Task.IsCompletedSuccessfully && sendCommandDetected.Task.Result;
                    }
                    var returnedLineBreakMarkers = VoiceCommandParser.CountExplicitLineBreakMarkers(response);
                    if (returnedLineBreakMarkers != explicitLineBreakCount ||
                        !VoiceCommandParser.HasExactlyOneResolvedMarkerPerCommand(response, voiceEditCommands))
                        throw new InvalidDataException("AIモデルが音声コマンドを適用できませんでした。指示の読み上げ方を変えて、もう一度お試しください。入力欄は変更していません。");
                    var responseWithVoiceOutcomes = VoiceCommandParser.RestoreVoiceCommandOutcomes(response, voiceEditCommands);
                    text = VoiceCommandParser.RestoreExplicitLineBreaks(responseWithVoiceOutcomes).Trim(' ', '\t', '\r');
                    if (!TranscriptFormatter.HasMeaningfulText(text))
                        throw new InvalidDataException("モデルから文字として使える回答が返りませんでした。入力欄は変更していません。");
                    RecordDictionaryTermUsage(relevantDictionary);
                    resultStatus = usePromptStyleRewrite
                        ? "プロンプト風に整えた文章を入力欄へ貼り付けました。"
                        : hasVoiceEditInstructions
                            ? "音声コマンドの編集指示を反映した文章を入力欄へ貼り付けました。"
                            : "整えた文章を入力欄へ貼り付けました。";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _openCode.Stop();
                    AudioInputDiagnosticLog.Write("ai-processing", "raw-transcript-fallback", ex, transcriptForPaste);
                    text = transcriptForPaste;
                    var sendWasRequested = sendAfterPaste;
                    sendAfterPaste = false;
                    rawFallbackUsed = true;
                    completionIndicator = "原文を反映";
                    resultStatus = sendWasRequested
                        ? "AI整形に失敗したため、文字起こしをそのまま貼り付けました。安全のため自動送信はしていません。"
                        : "AI整形に失敗したため、文字起こしをそのまま貼り付けました。";
                    aiFailureMessage = ex.GetBaseException().Message;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            pipelineStage = "output";
            outputAttempted = true;
            var outputResult = PasteAndShow(text, resultStatus);
            if (outputResult is null && TranscriptFormatter.HasMeaningfulText(_pendingRawTranscriptFallback))
            {
                var previousOutputFailure = RuntimeStatus.Text;
                var rawTranscript = _pendingRawTranscriptFallback!;
                if (!string.Equals(text, rawTranscript, StringComparison.Ordinal))
                {
                    text = rawTranscript;
                    sendAfterPaste = false;
                    rawFallbackUsed = true;
                    resultStatus = "貼り付けに失敗したため、文字起こしをそのまま貼り付けました。自動送信はしていません。";
                }
                AudioInputDiagnosticLog.Write("output-retry", "recognized-transcript", recognizedText: text);
                outputResult = PasteAndShow(text, resultStatus);
                if (!string.IsNullOrWhiteSpace(previousOutputFailure))
                    RuntimeStatus.Text += " 最初の出力に失敗したため再試行しました。";
            }
            var pasted = outputResult == PasteResult.Pasted;
            completionIndicator = rawFallbackUsed
                ? outputResult switch
                {
                    PasteResult.Pasted => "原文を反映",
                    PasteResult.CopiedOnly => "原文をコピー",
                    _ => "出力失敗"
                }
                : outputResult switch
                {
                    PasteResult.Pasted => "完了",
                    PasteResult.CopiedOnly => "コピーしました",
                    _ => "出力失敗"
                };
            if (rawFallbackUsed && !string.IsNullOrWhiteSpace(aiFailureMessage))
                RuntimeStatus.Text += " AIエラー: " + aiFailureMessage;
            pipelineStage = "post-output";
            if (pasted)
            {
                StartAutoDictionaryLearningMonitor(text);
                if (sendAfterPaste && _autoDictionaryLearningMonitor is { } learningMonitor)
                {
                    var monitorReady = await learningMonitor.WaitUntilReadyAsync(
                        TimeSpan.FromSeconds(4), cancellationToken);
                    if (!monitorReady)
                        DictionaryLearningDiagnosticLog.Write("monitor_not_ready_before_auto_send", "timeout");
                }
            }
            if (sendCommandMonitorUnavailable)
                RuntimeStatus.Text += " 音声の送信指示を待機できなかったため、Enterは送信していません。";
            if (sendAfterPaste)
            {
                if (!pasted)
                {
                    _indicator.ShowStatus("送信できません", TimeSpan.FromSeconds(1));
                    return;
                }
                await SendPastedTextAsync(cancellationToken);
                if (decoded.HadUnrecognizedVoiceCommand)
                    RuntimeStatus.Text += " 一部のSpace音声コマンドを認識できず、文字起こしには含めていません。";
                return;
            }

            if (decoded.HadUnrecognizedVoiceCommand)
                RuntimeStatus.Text += " 一部のSpace音声コマンドを認識できず、文字起こしには含めていません。";
            _indicator.ShowStatus(completionIndicator, TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_rawTranscriptFallbackRequested
                && !_closing
                && !string.IsNullOrWhiteSpace(_pendingRawTranscriptFallback))
            {
            if (sendCommandListenerTask is not null)
                {
                    await StopSendCommandMonitorAsync(sendCommandListenerTask);
                    if (ReferenceEquals(_sendCommandListenerTask, sendCommandListenerTask))
                        _sendCommandListenerTask = null;
                    sendCommandListenerTask = null;
                }

                if (!_rawTranscriptFallbackRequested)
                {
                    _indicator.ShowStatus("キャンセルしました", TimeSpan.FromSeconds(1));
                    return;
                }

                var rawTranscript = _pendingRawTranscriptFallback;
                outputAttempted = true;
                var outputResult = PasteAndShow(rawTranscript, "文字起こしを入力欄へ貼り付けました。");
                var pasted = outputResult == PasteResult.Pasted;
                if (pasted)
                {
                    StartAutoDictionaryLearningMonitor(rawTranscript);
                    if (sendAfterPaste)
                        await SendPastedTextAsync(CancellationToken.None);
                    else
                        _indicator.ShowStatus("文字起こしを反映", TimeSpan.FromSeconds(1));
                }
                else
                {
                    _indicator.ShowStatus(sendAfterPaste
                        ? "送信未完了"
                        : outputResult == PasteResult.CopiedOnly ? "文字起こしをコピー" : "コピー失敗",
                        TimeSpan.FromSeconds(1));
                }
            }
            else if (_closing) _indicator.HideStatus();
            else _indicator.ShowStatus("キャンセルしました", TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            var rawTranscript = _pendingRawTranscriptFallback;
            AudioInputDiagnosticLog.Write(pipelineStage, "failed", ex, rawTranscript);
            if (!_closing && !outputAttempted && TranscriptFormatter.HasMeaningfulText(rawTranscript))
            {
                var usableRawTranscript = rawTranscript!;
                outputAttempted = true;
                var outputResult = PasteAndShow(
                    usableRawTranscript,
                    "音声処理に失敗したため、認識できた文章をそのまま入力欄へ貼り付けました。");
                if (outputResult == PasteResult.Pasted)
                    StartAutoDictionaryLearningMonitor(usableRawTranscript);
                RuntimeStatus.Text += " 音声処理の失敗箇所: " + pipelineStage + "。詳細: " + ex.GetBaseException().Message;
                _indicator.ShowStatus(outputResult switch
                {
                    PasteResult.Pasted => "原文を反映",
                    PasteResult.CopiedOnly => "原文をコピー",
                    _ => "出力失敗"
                }, TimeSpan.FromSeconds(1));
            }
            else
            {
                RuntimeStatus.Text = ex.Message;
                _indicator.ShowStatus(ex.Message.Contains("文字起こし結果", StringComparison.Ordinal)
                    ? "音声認識失敗"
                    : "処理に失敗しました", TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            if (_continuousRecorder.IsRecording || sendCommandListenerTask is not null)
            {
                await StopSendCommandMonitorAsync(sendCommandListenerTask);
                if (ReferenceEquals(_sendCommandListenerTask, sendCommandListenerTask))
                    _sendCommandListenerTask = null;
            }
            ReleaseDiscordMuteForAudioSession();
            await StopTranscriptPreviewAsync();
            if (!_closing) _transcriptPreviewWindow.HidePreview();
            if (recordingSegments is not null)
            {
                foreach (var segment in recordingSegments)
                    Array.Clear(segment.Samples);
            }
            if (ReferenceEquals(_audioProcessingCancellation, processingCancellation))
                _audioProcessingCancellation = null;
            processingCancellation.Dispose();
            _normalAudioInputActive = false;
            _voiceCommandModeActive = false;
            _hotkey.SetVoiceCommandPushToTalkEnabled(false);
            _rawTranscriptFallbackRequested = false;
            _pendingRawTranscriptFallback = null;
            _hotkey.SetEscapeMonitoring(false);
            _isBusy = false;
            ActionButton.IsEnabled = true;
            ActionButton.Content = "録音を開始";
            UpdateAiModeDisplay();
        }
    }

    private async Task SendPastedTextAsync(CancellationToken cancellationToken)
    {
        var learningMonitor = _autoDictionaryLearningMonitor;
        if (learningMonitor is not null)
        {
            var monitorReady = await learningMonitor.WaitUntilReadyAsync(
                TimeSpan.FromSeconds(4), cancellationToken);
            if (!monitorReady)
                DictionaryLearningDiagnosticLog.Write("monitor_not_ready_before_auto_send", "timeout");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (PasteService.TryInvokeSendButton(_pasteTarget))
        {
            RuntimeStatus.Text = "文章を入力欄へ貼り付けて送信しました。";
            _indicator.ShowStatus("送信しました", TimeSpan.FromSeconds(1));
            return;
        }

        var learningPrepared = learningMonitor?.PrepareApplicationSubmit() == true;
        if (PasteService.SendEnterKey(_pasteTarget))
        {
            if (learningPrepared)
                learningMonitor?.NotifyApplicationSubmit();
            RuntimeStatus.Text = "文章を入力欄へ貼り付けてEnter送信しました。";
            _indicator.ShowStatus("送信しました", TimeSpan.FromSeconds(1));
            return;
        }

        learningMonitor?.CancelPreparedApplicationSubmit();
        RuntimeStatus.Text = "文章は貼り付けましたが、送信ボタンと入力欄のフォーカスを確認できず、送信していません。";
        _indicator.ShowStatus("送信未完了", TimeSpan.FromSeconds(1));
    }

    private async Task MonitorSendVoiceCommandsAsync(
        TaskCompletionSource<bool> sendCommandDetected,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var utterance in _continuousRecorder.ReadUtterancesAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                try
                {
                    if (sendCommandDetected.Task.IsCompleted) continue;
                    var recognizer = _asr;
                    if (recognizer is null) return;
                    try
                    {
                        var recognized = await Task.Run(() => recognizer.Transcribe(utterance), cancellationToken)
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        recognized = TranscriptFormatter.NormalizeTranscript(recognized);
                        if (VoiceCommandParser.IsStandaloneSendDirective(recognized))
                            sendCommandDetected.TrySetResult(true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // A single failed voice-command decode must not disable later commands.
                    }
                }
                finally
                {
                    Array.Clear(utterance);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // Voice command monitoring is best-effort and does not fail the text rewrite.
        }
    }

    private async Task StopSendCommandMonitorAsync(Task? monitorTask)
    {
        if (_continuousRecorder.IsRecording)
        {
            try { await _continuousRecorder.StopAsync(CancellationToken.None); }
            catch { }
        }

        if (monitorTask is not null)
        {
            try { await monitorTask; }
            catch { }
        }
    }

    private PasteResult? PasteAndShow(string text, string successStatus)
    {
        try
        {
            var paste = PasteService.CopyAndPaste(text, _pasteTarget);
            AudioInputDiagnosticLog.Write("output", paste == PasteResult.Pasted ? "pasted" : "copied-only", recognizedText: text);
            RuntimeStatus.Text = paste == PasteResult.Pasted
                ? successStatus
                : successStatus + " 自動貼り付けを確認できないため、結果はクリップボードにコピーしました。必要ならCtrl+Vで入力してください。";
            return paste;
        }
        catch (Exception ex)
        {
            AudioInputDiagnosticLog.Write("output", "failed", ex, text);
            RuntimeStatus.Text = "自動貼り付けまたはクリップボードへのコピーに失敗しました。 " + ex.Message;
            return null;
        }
    }

    private void ShortcutChangeButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_asr is null || _isBusy || _isCapturingShortcut || _isCapturingDiscordMuteShortcut) return;

        _isCapturingShortcut = true;
        _hotkey.Unregister();
        _hotkeyRegistered = false;
        ShortcutChangeButton.Content = "入力中…";
        ShortcutCaptureStatus.Text = "右Alt単独、またはCtrl / Shift / Altなどとの組み合わせを押してください。Escでキャンセルできます。";
        ShortcutChangeButton.Focus();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_isCapturingDiscordMuteShortcut)
        {
            HandleDiscordMuteShortcutCapture(key);
            e.Handled = true;
            return;
        }

        if (!_isCapturingShortcut) return;

        if (key == Key.Escape)
        {
            CancelShortcutCapture();
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var otherModifiers = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows;
        if (key == Key.RightAlt
            && (modifiers & otherModifiers) == ModifierKeys.None
            && !Keyboard.IsKeyDown(Key.LeftAlt))
        {
            ApplyShortcut(new HotkeyBinding(0, HotkeyBinding.RightAltVirtualKey));
            e.Handled = true;
            return;
        }

        if (IsModifierKey(key))
        {
            ShortcutCaptureStatus.Text = $"{FormatPressedModifiers(modifiers)}を押しています。続けて組み合わせるキーを押してください。";
            e.Handled = true;
            return;
        }

        var nativeModifiers = ToNativeModifiers(modifiers);
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (nativeModifiers == 0)
        {
            ShortcutCaptureStatus.Text = "単独キーは登録できません。右Alt単独、または修飾キーとの組み合わせを使ってください。";
        }
        else if (virtualKey > 0)
        {
            ApplyShortcut(new HotkeyBinding(nativeModifiers, (uint)virtualKey));
        }

        e.Handled = true;
    }

    private void ApplyShortcut(HotkeyBinding binding)
    {
        var current = GetConfiguredHotkey();
        var handle = new WindowInteropHelper(this).Handle;
        if (!_hotkey.Register(handle, binding))
        {
            var restored = _hotkey.Register(handle, current);
            if (restored)
            {
                _hotkey.Unregister();
            }
            _hotkeyRegistered = false;
            ShortcutCaptureStatus.Text = "このショートカットは登録できませんでした。別のキーを試してください。";
            return;
        }

        var oldModifiers = _settings.ShortcutModifiers;
        var oldVirtualKey = _settings.ShortcutVirtualKey;
        _settings.ShortcutModifiers = binding.Modifiers;
        _settings.ShortcutVirtualKey = binding.VirtualKey;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.ShortcutModifiers = oldModifiers;
            _settings.ShortcutVirtualKey = oldVirtualKey;
            _hotkeyRegistered = _hotkey.Register(handle, current);
            _isCapturingShortcut = false;
            ShortcutChangeButton.Content = "変更";
            UpdateShortcutDisplay();
            ShortcutCaptureStatus.Text = "設定を保存できませんでした。以前のショートカットに戻しました。";
            RuntimeStatus.Text = ex.Message;
            return;
        }

        _hotkeyRegistered = true;
        _isCapturingShortcut = false;
        ShortcutChangeButton.Content = "変更";
        UpdateShortcutDisplay();
        ShortcutCaptureStatus.Text = string.Empty;
        RuntimeStatus.Text = string.Empty;
    }

    private void CancelShortcutCapture()
    {
        _isCapturingShortcut = false;
        _hotkeyRegistered = _hotkey.Register(new WindowInteropHelper(this).Handle, GetConfiguredHotkey());
        ShortcutChangeButton.Content = "変更";
        ShortcutCaptureStatus.Text = "変更をキャンセルしました。";
    }

    private HotkeyBinding GetConfiguredHotkey()
    {
        var binding = new HotkeyBinding(_settings.ShortcutModifiers, _settings.ShortcutVirtualKey);
        return binding.IsUsable ? binding : new HotkeyBinding(0, HotkeyBinding.RightAltVirtualKey);
    }

    private void UpdateShortcutDisplay()
    {
        var shortcut = GlobalHotkey.Format(GetConfiguredHotkey());
        ShortcutCurrentText.Text = shortcut;
        System.Windows.Automation.AutomationProperties.SetName(
            ShortcutChangeButton, $"ショートカットキーを変更。現在: {shortcut}");
    }

    private void RegisterConfiguredHotkey()
    {
        _hotkey.Unregister();
        _hotkeyRegistered = false;
        if (_asr is null || _exitRequested) return;

        var helper = new WindowInteropHelper(this);
        var handle = helper.EnsureHandle();
        AttachWindowMessageHook(handle);
        _hotkeyRegistered = _hotkey.Register(handle, GetConfiguredHotkey());
    }

    private void AttachWindowMessageHook(IntPtr handle)
    {
        var source = HwndSource.FromHwnd(handle);
        if (source is null) return;

        if (!ReferenceEquals(_source, source))
        {
            if (_windowMessageHookAttached)
                _source?.RemoveHook(WindowMessageHook);
            _source = source;
            _windowMessageHookAttached = false;
        }

        if (_windowMessageHookAttached) return;
        _source.AddHook(WindowMessageHook);
        _windowMessageHookAttached = true;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint native = 0;
        if ((modifiers & ModifierKeys.Alt) != 0) native |= 0x0001;
        if ((modifiers & ModifierKeys.Control) != 0) native |= 0x0002;
        if ((modifiers & ModifierKeys.Shift) != 0) native |= 0x0004;
        if ((modifiers & ModifierKeys.Windows) != 0) native |= 0x0008;
        return native;
    }

    private static string FormatPressedModifiers(ModifierKeys modifiers)
    {
        var names = new List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) names.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift) != 0) names.Add("Shift");
        if ((modifiers & ModifierKeys.Alt) != 0) names.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) names.Add("Windows");
        return names.Count == 0 ? "修飾キー" : string.Join(" + ", names);
    }

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        CancelDiscordMuteShortcutCapture();
        Close();
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _startupMenuItem = new ToolStripMenuItem("Windows起動時に自動起動")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled
        };
        _startupMenuItem.CheckedChanged += StartupMenuItem_CheckedChanged;

        var exitMenuItem = new ToolStripMenuItem("アプリを完全に終了する");
        exitMenuItem.Click += (_, _) => ExitApplication();

        _trayMenu.Items.Add(_startupMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitMenuItem);
        _trayMenu.Opening += (_, _) => RefreshStartupMenuItem();

        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
                _ownedTrayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
        }
        catch { }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _ownedTrayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "Better Flow",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void StartupMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        if (_syncingStartupMenu || _startupMenuItem is null) return;

        try
        {
            StartupRegistration.SetEnabled(_startupMenuItem.Checked);
        }
        catch (Exception ex)
        {
            _syncingStartupMenu = true;
            _startupMenuItem.Checked = !_startupMenuItem.Checked;
            _syncingStartupMenu = false;
            System.Windows.MessageBox.Show(
                "Windows起動時の自動起動設定を変更できませんでした。\n" + ex.Message,
                "Better Flow",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void RefreshStartupMenuItem()
    {
        if (_startupMenuItem is null) return;
        _syncingStartupMenu = true;
        _startupMenuItem.Checked = StartupRegistration.IsEnabled;
        _syncingStartupMenu = false;
    }

    private void HideToTray()
    {
        if (_isCapturingShortcut)
            CancelShortcutCapture();
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_exitRequested) return;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void SystemEvents_SessionEnding(object? sender, SessionEndingEventArgs e) => _exitRequested = true;

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkey.IsVoiceCommandStartMessage(msg))
        {
            handled = true;
            BeginVoiceCommandCapture();
            return IntPtr.Zero;
        }

        if (_hotkey.IsVoiceCommandStopMessage(msg))
        {
            handled = true;
            EndVoiceCommandCaptureIfActive();
            return IntPtr.Zero;
        }

        if (_hotkey.IsEscapeCancelMessage(msg))
        {
            handled = true;
            _ = HandleEscapeCancellationAsync();
            return IntPtr.Zero;
        }

        if (msg == GlobalHotkey.MessageId && _hotkey.IsMessage(wParam.ToInt32()))
        {
            handled = true;
            ActionButton_Click(this, new System.Windows.RoutedEventArgs());
        }
        return IntPtr.Zero;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            CancelDiscordMuteShortcutCapture();
            e.Cancel = true;
            HideToTray();
            return;
        }

        _closing = true;
        _lifetime.Cancel();
        CancelTranscriptPreview();
        _autoDictionaryLearningMonitor?.Dispose();
        _selectionEditCancellation?.Cancel();
        _selectedTextQuestionCancellation?.Cancel();
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _hotkey.Dispose();
        ReleaseDiscordMuteForAudioSession();
        _discordPushToMute.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _indicator.Close();
        _transcriptPreviewWindow.Close();
        _undoWindow.Close();
        _answerWindow.Close();
        _recorder.Dispose();
        try { _transcriptPreviewTask?.GetAwaiter().GetResult(); } catch { }
        _continuousRecorder.Dispose();
        try { _sendCommandListenerTask?.GetAwaiter().GetResult(); } catch { }
        _activationSoundPlayer.Dispose();
        _asr?.Dispose();
        _openCode.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
        _ownedTrayIcon?.Dispose();
        _lifetime.Dispose();
    }
}

internal readonly record struct NormalAudioDecodeResult(
    string Transcript,
    bool FormatAsPrompt,
    bool SendAsIs,
    IReadOnlyList<string> EditInstructions,
    IReadOnlyList<VoiceEditCommandInstruction> VoiceEditCommands,
    bool HadUnrecognizedVoiceCommand);
