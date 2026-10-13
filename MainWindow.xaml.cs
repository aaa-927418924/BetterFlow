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
    private readonly IndicatorWindow _indicator;
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
    private bool _selectionEditSessionActive;
    private bool _selectionEditSessionStopping;
    private CancellationTokenSource? _selectionEditCancellation;
    private IReadOnlyList<OpenCodeModelOption> _openCodeModels = Array.Empty<OpenCodeModelOption>();
    private IReadOnlyList<AudioInputDeviceOption> _audioInputDevices = Array.Empty<AudioInputDeviceOption>();

    private bool IsAnyRecording => _recorder.IsRecording || _continuousRecorder.IsRecording || _selectionEditSessionActive;

    public MainWindow()
    {
        InitializeComponent();
        _indicator = new IndicatorWindow();
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
    }

    private async void ActionButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectionEditSessionActive)
        {
            await RecordButton_Click();
            return;
        }
        if (_isBusy) return;
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
    }

    private static string DescribeSetupError(Exception exception)
    {
        var root = exception.GetBaseException();
        if (root is IOException io && (io.HResult & 0xFFFF) is 32 or 33)
            return "ファイルが別の処理で使用中です。実行中のセットアップがあれば完了を待ち、他のアプリで対象ファイルを開いていないか確認して再試行してください。\n" + root.Message;
        return root.Message;
    }

    private async Task EnsureOpenCodeReadyAsync()
    {
        var progress = new Progress<SetupProgress>(update => RuntimeStatus.Text = update.Message);
        await new OpenCodeInstaller().EnsureInstalledAsync(progress, _lifetime.Token);
        RuntimeStatus.Text = "OpenCodeを起動しています…";
        await Task.Run(() => _openCode.StartAsync(_lifetime.Token), _lifetime.Token);
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
            UpdateAiModeDisplay();
            RuntimeStatus.Text = "AIモードに切り替えました。";
        }
        catch (Exception ex)
        {
            _settings.AiModeEnabled = false;
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
        AiModeDescription.Text = _settings.AiModeEnabled
            ? "オン: OpenCodeの選択モデルで文章を整えます。"
            : "オフ: 文字起こしをそのまま貼り付けます。";
        PrivacyNotice.Text = _settings.AiModeEnabled
            ? "音声はPC内で文字起こしします。AIモードでは文字起こしと、選択文の編集時に選択中の文章・音声指示をOpenCode経由でモデルへ送信します。"
            : "音声と文字起こしはPC内で処理します。AIモードを使うときだけ文字起こしテキストをOpenCode経由で送信します。";
        AiModeToggle.IsEnabled = _asr is not null && !_isBusy && !IsAnyRecording && !_isRefreshingOpenCodeModels;
        var canChooseModel = !_settings.AiModeEnabled && !_isBusy && !IsAnyRecording;
        OpenCodeModelCombo.IsEnabled = canChooseModel;
        RefreshOpenCodeModelsButton.IsEnabled = canChooseModel && !_isRefreshingOpenCodeModels;
        OpenCodeModelModeHint.Visibility = _settings.AiModeEnabled
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
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
            : $"保存済みの選択: {selected.DisplayName}。接続状態は一覧更新後に確認します。";
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
            : $"選択中: {selected.DisplayName}";
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
        BodyText.Text = "入力欄にカーソルを置いてショートカットを押すと音声入力を始めます。選択文がある状態で押すと、その文章を音声指示で編集します。編集セッション中は話すだけで続けて適用され、同じショートカットで終了します。";
        CapacityText.Visibility = System.Windows.Visibility.Collapsed;
        ShortcutSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        MicrophoneSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        OpenCodeModelSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        ShortcutCaptureStatus.Text = string.Empty;
        UpdateShortcutDisplay();
        UpdateAiModeDisplay();
        PrivacyNotice.Visibility = System.Windows.Visibility.Visible;
        ActionButton.Visibility = System.Windows.Visibility.Collapsed;
        RuntimeStatus.Text = _hotkeyRegistered ? "準備完了" : "ショートカットが登録されていません。別のキーを設定してください。";
    }

    private async Task RecordButton_Click()
    {
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
                    if (!_settings.AiModeEnabled)
                    {
                        RuntimeStatus.Text = "選択文の音声編集にはAIモードをオンにしてください。";
                        _indicator.ShowStatus("AIモードをオン", TimeSpan.FromSeconds(1));
                        return;
                    }

                    BeginSelectionEditSession(selection!);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(selectionError))
                {
                    RuntimeStatus.Text = selectionError;
                    _indicator.ShowStatus("選択を確認", TimeSpan.FromSeconds(1));
                    return;
                }

                var selectedInput = MicrophoneDeviceCombo.SelectedItem as AudioInputDeviceOption;
                StartRecorderWithFallback(_recorder.Start, selectedInput);
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
            RuntimeStatus.Text = "選択文を編集します。続けて話してください。ショートカットで終了できます。";
            ActionButton.Content = "編集セッションを終了";
            _indicator.ShowStatus("選択文編集中");
            UpdateAiModeDisplay();
            _ = ProcessSelectionEditSessionAsync(context, cancellation.Token);
        }
        catch
        {
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
        try
        {
            await foreach (var samples in _continuousRecorder.ReadUtterancesAsync(cancellationToken))
            {
                _isBusy = true;
                ActionButton.Content = "選択文を編集しています…";
                RuntimeStatus.Text = "発話を文字起こししています…";
                _indicator.ShowStatus("音声認識中");
                try
                {
                    var transcript = await Task.Run(() => _asr!.Transcribe(samples), cancellationToken);
                    transcript = TranscriptFormatter.NormalizeTranscript(transcript);
                    if (!TranscriptFormatter.HasMeaningfulText(transcript))
                    {
                        RuntimeStatus.Text = "音声を認識できませんでした。続けて話してください。";
                        _indicator.ShowStatus("選択文編集中");
                        continue;
                    }

                    _indicator.ShowStatus("AI編集中");
                    RuntimeStatus.Text = "選択文へ音声指示を適用しています…";
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync();
                    var replacement = await _openCode.EditSelectedTextAsync(
                        context.CurrentText,
                        transcript,
                        _settings.OpenCodeModelProviderId,
                        _settings.OpenCodeModelId,
                        cancellationToken);

                    var result = await SelectionEditService.ReplaceCurrentSelectionAsync(
                        context, replacement, cancellationToken);
                    RuntimeStatus.Text = result.Message;
                    _indicator.ShowStatus(result.CanContinue ? "選択文編集中" : "編集を中止",
                        result.CanContinue ? null : TimeSpan.FromSeconds(1));

                    if (!result.CanContinue)
                    {
                        _selectionEditSessionStopping = true;
                        await _continuousRecorder.StopAsync(_lifetime.Token);
                        break;
                    }
                }
                catch (OperationCanceledException) when (_closing)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _openCode.Stop();
                    RuntimeStatus.Text = "AI編集に失敗しました。選択文は変更していません。 " + ex.GetBaseException().Message;
                    _indicator.ShowStatus("選択文編集中");
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
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "連続音声の処理を終了しました。 " + ex.GetBaseException().Message;
            _indicator.ShowStatus("処理を終了", TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (_continuousRecorder.IsRecording)
            {
                try { await _continuousRecorder.StopAsync(CancellationToken.None); }
                catch { }
            }
            _continuousRecorder.DiscardPendingUtterances();

            var endedDuringShutdown = _closing || cancellationToken.IsCancellationRequested;
            _selectionEditSessionActive = false;
            _selectionEditSessionStopping = false;
            _isBusy = false;
            _selectionEditCancellation?.Dispose();
            _selectionEditCancellation = null;
            ActionButton.Content = _asr is null ? "Setup" : "録音を開始";
            if (endedDuringShutdown) _indicator.HideStatus();
            else _indicator.ShowStatus("編集終了", TimeSpan.FromSeconds(1));
            UpdateAiModeDisplay();
        }
    }

    private async Task StopAndProcessAsync()
    {
        _isBusy = true;
        UpdateAiModeDisplay();
        ActionButton.IsEnabled = false;
        ActionButton.Content = "音声を文字起こししています…";
        RuntimeStatus.Text = "音声をPC内で文字起こししています…";
        _indicator.ShowStatus("認識中");
        float[]? samples = null;
        try
        {
            samples = await _recorder.StopAsync(_lifetime.Token);
            var transcript = await Task.Run(() => _asr!.Transcribe(samples), _lifetime.Token);
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
                    if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync();
                    var response = await _openCode.RewriteAsync(
                        transcript, _settings.OpenCodeModelProviderId, _settings.OpenCodeModelId, _lifetime.Token);
                    text = response.Trim();
                    if (!TranscriptFormatter.HasMeaningfulText(text))
                        throw new InvalidDataException("モデルから文字として使える回答が返りませんでした。入力欄は変更していません。");
                    resultStatus = "整えた文章を入力欄へ貼り付けました。";
                }
                catch (OperationCanceledException) when (_closing) { throw; }
                catch (Exception ex)
                {
                    _openCode.Stop();
                    RuntimeStatus.Text = "AI変換に失敗しました。入力欄は変更していません。 " + ex.Message;
                    _indicator.ShowStatus(GetAiFailureIndicatorText(ex), TimeSpan.FromSeconds(1));
                    return;
                }
            }

            PasteAndShow(text, resultStatus);
            _indicator.ShowStatus("完了", TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (_closing) { _indicator.HideStatus(); }
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
                : successStatus + " 貼り付け先が変わったため、Ctrl+Vで入力してください。";
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
        ShortcutCaptureStatus.Text = "ショートカットを保存しました。";
        RuntimeStatus.Text = "準備完了";
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
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _hotkey.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _indicator.Close();
        _recorder.Dispose();
        _continuousRecorder.Dispose();
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
