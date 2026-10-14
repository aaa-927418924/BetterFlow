using System.IO;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Input;
using BetterFlow.Services;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace BetterFlow;

public partial class MainWindow : System.Windows.Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly MicrophoneRecorder _recorder = new();
    private readonly ContinuousMicrophoneRecorder _continuousRecorder = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OpenCodeService _openCode = new();
    private readonly GptLiveAuthService _gptLiveAuth = new();
    private readonly ActivationSoundPlayer _activationSoundPlayer = new();
    private readonly IndicatorWindow _indicator;
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
    private bool _closing;
    private bool _hotkeyRegistered;
    private bool _windowMessageHookAttached;
    private bool _exitRequested;
    private bool _syncingStartupMenu;
    private bool _isCapturingShortcut;
    private bool _isRefreshingOpenCodeModels;
    private bool _loadingOpenCodeModelUi;
    private bool _loadingMicrophoneDeviceUi;
    private bool _openCodeReady;
    private CancellationTokenSource? _audioProcessingCancellation;
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
        _answerWindow = new AnswerWindow();
        _recorder.AudioLevelChanged += _indicator.SetAudioLevel;
        _continuousRecorder.AudioLevelChanged += _indicator.SetAudioLevel;
        _undoWindow = new UndoWindow();
        _undoWindow.UndoRequested += UndoWindow_UndoRequested;
        _undoWindow.UndoExpired += UndoWindow_UndoExpired;
        _startHiddenInTray = Bootstrapper.IsModelReady();
        if (_startHiddenInTray)
        {
            TitleText.Text = "アプリを起動しています…";
            BodyText.Text = "音声モデルを確認しています。";
            CapacityText.Text = string.Empty;
        }
        _settings = _settingsStore.Load();
        NormalizeAiSettings();
        InitializeOpenCodeModelUi();
        InitializeSelectedTextAnswerModeUi();
        InitializeMicrophoneDeviceUi();
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
        if (_closing || !_settings.AiModeEnabled || _asr is null || _openCodeReady || _isBusy)
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
            RuntimeStatus.Text = "OpenCodeモデルを準備できませんでした。設定を確認し、AIモードを一度オフにしてからオンにしてください。 " + ex.GetBaseException().Message;
        }
        finally
        {
            _isBusy = false;
            UpdateAiModeDisplay();
        }
    }

    private async void AiModeToggle_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var enableAiMode = AiModeToggle.IsChecked == true;
        if (_asr is null || _isBusy || IsAnyRecording)
        {
            AiModeToggle.IsChecked = _settings.AiModeEnabled;
            RuntimeStatus.Text = IsAnyRecording
                ? "録音中はモードを切り替えられません。"
                : "処理中はモードを切り替えられません。";
            return;
        }

        if (enableAiMode == _settings.AiModeEnabled) return;

        if (!enableAiMode)
        {
            _settings.AiModeEnabled = false;
            try
            {
                _settingsStore.Save(_settings);
                _openCodeReady = false;
                _openCode.Stop();
                UpdateAiModeDisplay();
                RuntimeStatus.Text = "文字起こしのみモードに切り替えました。";
            }
            catch (Exception ex)
            {
                _settings.AiModeEnabled = true;
                AiModeToggle.IsChecked = true;
                UpdateAiModeDisplay();
                RuntimeStatus.Text = "モード設定を保存できませんでした。 " + ex.Message;
            }
            return;
        }

        _isBusy = true;
        _openCodeReady = false;
        UpdateAiModeDisplay();
        AiModeToggle.IsEnabled = false;
        ActionButton.IsEnabled = false;
        RuntimeStatus.Text = "OpenCodeモデルを確認しています…";
        try
        {
            await EnsureOpenCodeReadyAsync();
            await LoadOpenCodeModelsAsync();
            var selectedModel = GetSelectedOpenCodeModel();
            if (selectedModel is null)
                throw new InvalidOperationException("OpenCodeのモデル一覧から使うモデルを選択してください。OpenCode Desktopでモデルを追加した後は一覧を更新してください。");
            await _openCode.EnsureModelAvailableAsync(selectedModel.ProviderId, selectedModel.ModelId, _lifetime.Token);
            SaveOpenCodeModelSelection(selectedModel);
            _settings.AiModeEnabled = true;
            _settingsStore.Save(_settings);
            _openCodeReady = true;
            UpdateAiModeDisplay();
            RuntimeStatus.Text = "AIモードに切り替えました。";
        }
        catch (Exception ex)
        {
            _settings.AiModeEnabled = false;
            _openCodeReady = false;
            AiModeToggle.IsChecked = false;
            _openCode.Stop();
            UpdateAiModeDisplay();
            RuntimeStatus.Text = "AIモードを有効にできませんでした。文字起こしのみで利用できます。 " + ex.Message;
        }
        finally
        {
            _isBusy = false;
            AiModeToggle.IsEnabled = true;
            ActionButton.IsEnabled = true;
            UpdateAiModeDisplay();
        }
    }

    private void UpdateAiModeDisplay()
    {
        AiModeToggle.IsChecked = _settings.AiModeEnabled;
        AiModeDescription.Text = !_settings.AiModeEnabled
            ? "オフ: 文字起こしをそのまま貼り付けます。"
            : "オン: OpenCodeの選択モデルで文章を整えます。";
        AiModeToggle.IsEnabled = _asr is not null && !_isBusy && !IsAnyRecording && !_isRefreshingOpenCodeModels;
        var canChooseModel = !_settings.AiModeEnabled && !_isBusy && !IsAnyRecording;
        OpenCodeModelCombo.IsEnabled = canChooseModel;
        RefreshOpenCodeModelsButton.IsEnabled = canChooseModel && !_isRefreshingOpenCodeModels;
        OpenCodeModelModeHint.Visibility = _settings.AiModeEnabled
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        UpdateSelectedTextAnswerModeDisplay();
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
                _settings.OpenCodeModelId = "muse-spark-1.3-contributor-free";
            }

            if (oldProviderId is not ("" or "opencode-zen-free"))
                _settings.AiModeEnabled = false;
            _settings.LegacyAiProviderId = null;
            _settings.LegacyAiProviders = null;
            shouldSaveSettings = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelProviderId) && !resolveLegacyModelById)
        {
            _settings.OpenCodeModelProviderId = "opencode";
            shouldSaveSettings = true;
        }
        if (string.IsNullOrWhiteSpace(_settings.OpenCodeModelId))
        {
            _settings.OpenCodeModelId = "muse-spark-1.3-contributor-free";
            shouldSaveSettings = true;
        }
        if (!string.Equals(_settings.SelectionEditEngine, "opencode", StringComparison.Ordinal))
        {
            _settings.SelectionEditEngine = "opencode";
            shouldSaveSettings = true;
        }

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

    private void OpenCodeModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOpenCodeModelUi) return;
        if (_settings.AiModeEnabled || _isBusy || IsAnyRecording)
        {
            _loadingOpenCodeModelUi = true;
            OpenCodeModelCombo.SelectedItem = FindSelectedOpenCodeModel();
            _loadingOpenCodeModelUi = false;
            RuntimeStatus.Text = "モデルを変更するには、録音や処理を終えてAIモードをオフにしてください。";
            return;
        }

        if (GetSelectedOpenCodeModel() is not OpenCodeModelOption selected) return;
        var oldProviderId = _settings.OpenCodeModelProviderId;
        var oldModelId = _settings.OpenCodeModelId;
        try
        {
            SaveOpenCodeModelSelection(selected);
            _settingsStore.Save(_settings);
            UpdateAiModeDisplay();
            RuntimeStatus.Text = "OpenCodeモデルを選択しました。";
        }
        catch (Exception ex)
        {
            _settings.OpenCodeModelProviderId = oldProviderId;
            _settings.OpenCodeModelId = oldModelId;
            _loadingOpenCodeModelUi = true;
            OpenCodeModelCombo.SelectedItem = FindSelectedOpenCodeModel();
            _loadingOpenCodeModelUi = false;
            RuntimeStatus.Text = "モデルを選択できませんでした。 " + ex.Message;
        }
    }

    private async void RefreshOpenCodeModels_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings.AiModeEnabled || _isBusy || IsAnyRecording)
        {
            OpenCodeModelStatus.Text = "モデル一覧を更新するには、AIモードをオフにしてください。";
            return;
        }

        var wasRunning = _openCode.IsRunning;
        _isRefreshingOpenCodeModels = true;
        UpdateAiModeDisplay();
        OpenCodeModelStatus.Text = "OpenCode Desktopで接続中のproviderを確認しています…";
        try
        {
            await EnsureOpenCodeReadyAsync();
            await LoadOpenCodeModelsAsync();
            var selected = GetSelectedOpenCodeModel();
            OpenCodeModelStatus.Text = _openCodeModels.Count == 0
                ? "接続済みモデルが見つかりません。OpenCode Desktopでproviderに接続してから更新してください。"
                : IsSelectedOpenCodeModelConnected(selected)
                    ? $"接続済みproviderのモデルを{_openCodeModels.Count}件読み込みました。"
                    : $"接続済みproviderのモデルを{_openCodeModels.Count}件読み込みました。保存中のモデルは未接続です。接続後に選び直してください。";
        }
        catch (Exception ex)
        {
            OpenCodeModelStatus.Text = "モデル一覧を読み込めませんでした。 " + ex.Message;
        }
        finally
        {
            if (!wasRunning && !_settings.AiModeEnabled) _openCode.Stop();
            _isRefreshingOpenCodeModels = false;
            UpdateAiModeDisplay();
        }
    }

    private async Task LoadRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = await Task.Run(() => AsrService.Load(AppPaths.ModelFile, AppPaths.TokensFile), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _asr?.Dispose();
        _asr = recognizer;
        RegisterConfiguredHotkey();
        TitleText.Visibility = System.Windows.Visibility.Collapsed;
        BodyText.Text = "入力欄で押すと音声入力を始めます。編集できる文章を選択中なら音声で編集し、通常の文章を選択中ならその文章について質問できます。";
        CapacityText.Visibility = System.Windows.Visibility.Collapsed;
        ShortcutSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        MicrophoneSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        OpenCodeModelSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        SelectedTextAnswerSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        ShortcutCaptureStatus.Text = string.Empty;
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
                if (SelectionEditService.TryCapture(out var selection, out var selectionError))
                {
                    var selectedTextContext = selection!;
                    if (selectedTextContext.IsEditable)
                    {
                        if (!_settings.AiModeEnabled)
                        {
                            RuntimeStatus.Text = "選択した入力文章を編集するにはAIモードをオンにしてください。";
                            _indicator.ShowStatus("AIモードをオン", TimeSpan.FromSeconds(1));
                            return;
                        }
                        if (!_openCodeReady)
                        {
                            RuntimeStatus.Text = "OpenCodeモデルの準備が完了していません。AIモードを一度オフにしてからオンにしてください。";
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

                if (_settings.AiModeEnabled && !_openCodeReady)
                {
                    RuntimeStatus.Text = "OpenCodeモデルの準備が完了していません。AIモードを一度オフにしてからオンにしてください。";
                    _indicator.ShowStatus("AI準備未完了", TimeSpan.FromSeconds(1));
                    return;
                }

                var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
                StartRecorderWithFallback(_recorder.Start, selectedInput);
                _activationSoundPlayer.Play();
                ActionButton.Content = "録音を停止して変換";
                RuntimeStatus.Text = "録音中 · もう一度ショートカットで停止";
                _indicator.ShowStatus("録音中");
                UpdateAiModeDisplay();
            }
            catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
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
            StartRecorderWithFallback(_continuousRecorder.Start, selectedInput);
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

    private void BeginSelectionEditSession(SelectionEditContext context)
    {
        var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            StartRecorderWithFallback(_continuousRecorder.Start, selectedInput);
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
            cancellation.Dispose();
            throw;
        }
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

    private async Task StopAndProcessAsync()
    {
        var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _audioProcessingCancellation = processingCancellation;
        var cancellationToken = processingCancellation.Token;
        _isBusy = true;
        UpdateAiModeDisplay();
        ActionButton.IsEnabled = false;
        ActionButton.Content = "音声を文字起こししています…";
        RuntimeStatus.Text = "音声をPC内で文字起こししています…";
        _indicator.ShowStatus("認識中");
        float[]? samples = null;
        try
        {
            samples = await _recorder.StopAsync(cancellationToken);
            var transcript = await Task.Run(() => _asr!.Transcribe(samples), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(samples);
            samples = null;
            transcript = TranscriptFormatter.NormalizeTranscript(transcript);
            if (!TranscriptFormatter.HasMeaningfulText(transcript))
                throw new InvalidDataException("文字起こし結果が空、または句読点・記号だけでした。マイクを確認して、もう一度お試しください。");

            string text;
            string resultStatus;
            _indicator.HideStatus();
            if (!_settings.AiModeEnabled)
            {
                text = TranscriptFormatter.NormalizeTranscript(transcript);
                resultStatus = "文字起こしを入力欄へ貼り付けました。";
            }
            else
            {
                RuntimeStatus.Text = "OpenCodeを準備しています…";
                _indicator.ShowStatus("整形中");
                try
                {
                    RuntimeStatus.Text = "OpenCodeの選択モデルで文章を整えています…";
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync(cancellationToken);
                    var response = await _openCode.RewriteAsync(
                        transcript, _settings.OpenCodeModelProviderId, _settings.OpenCodeModelId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    text = response.Trim();
                    if (!TranscriptFormatter.HasMeaningfulText(text))
                        throw new InvalidDataException("モデルから文字として使える回答が返りませんでした。入力欄は変更していません。");
                    resultStatus = "整えた文章を入力欄へ貼り付けました。";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _openCode.Stop();
                    RuntimeStatus.Text = "AI変換に失敗しました。入力欄は変更していません。 " + ex.Message;
                    _indicator.ShowStatus(GetAiFailureIndicatorText(ex), TimeSpan.FromSeconds(1));
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            PasteAndShow(text, resultStatus);
            _indicator.ShowStatus("完了", TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_closing) _indicator.HideStatus();
            else _indicator.ShowStatus("キャンセルしました", TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            RuntimeStatus.Text = ex.Message;
            _indicator.ShowStatus(ex.Message.Contains("文字起こし結果", StringComparison.Ordinal)
                ? "音声認識失敗"
                : "処理に失敗しました", TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (samples is not null) Array.Clear(samples);
            if (ReferenceEquals(_audioProcessingCancellation, processingCancellation))
                _audioProcessingCancellation = null;
            processingCancellation.Dispose();
            _isBusy = false;
            ActionButton.IsEnabled = true;
            ActionButton.Content = "録音を開始";
            UpdateAiModeDisplay();
        }
    }

    private static string GetAiFailureIndicatorText(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (message.Contains("OAuth認証を更新", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Token refresh failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "OpenCode再ログイン";
        if (message.Contains("文字として使える回答", StringComparison.Ordinal))
            return "AI応答なし";
        return "AI変換失敗";
    }

    private void PasteAndShow(string text, string successStatus)
    {
        try
        {
            var paste = PasteService.CopyAndPaste(text, _pasteTarget);
            RuntimeStatus.Text = paste == PasteResult.Pasted
                ? successStatus
                : successStatus + " 自動貼り付けを確認できないため、結果はクリップボードにコピーしました。必要ならCtrl+Vで入力してください。";
        }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "自動貼り付けまたはクリップボードへのコピーに失敗しました。 " + ex.Message;
        }
    }

    private void ShortcutChangeButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_asr is null || _isBusy || _isCapturingShortcut) return;

        _isCapturingShortcut = true;
        _hotkey.Unregister();
        _hotkeyRegistered = false;
        ShortcutChangeButton.Content = "入力中…";
        ShortcutCaptureStatus.Text = "右Alt単独、またはCtrl / Shift / Altなどとの組み合わせを押してください。Escでキャンセルできます。";
        ShortcutChangeButton.Focus();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingShortcut) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
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

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

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
            e.Cancel = true;
            HideToTray();
            return;
        }

        _closing = true;
        _lifetime.Cancel();
        _selectionEditCancellation?.Cancel();
        _selectedTextQuestionCancellation?.Cancel();
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _hotkey.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _indicator.Close();
        _undoWindow.Close();
        _answerWindow.Close();
        _recorder.Dispose();
        _continuousRecorder.Dispose();
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
