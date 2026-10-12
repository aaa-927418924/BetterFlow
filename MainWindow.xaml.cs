using System.IO;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Input;
using AutoPrompt.Services;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace AutoPrompt;

public partial class MainWindow : System.Windows.Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly MicrophoneRecorder _recorder = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OpenCodeService _openCode = new();
    private readonly AiModelService _aiModelService = new();
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
    private bool _loadingAiProviderUi;
    private IReadOnlyList<ZenModelOption> _zenModelOptions = Array.Empty<ZenModelOption>();

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
        InitializeAiProviderUi();
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
        try
        {
            var progress = new Progress<SetupProgress>(update =>
            {
                currentStage = update.Message.TrimEnd('…', '.');
                SetupStatus.Text = update.Message;
                SetupProgress.Value = Math.Clamp(update.Percent, 0, 100);
            });
            await new Bootstrapper().EnsureModelAsync(progress, _lifetime.Token);
            currentStage = "音声モデルの起動";
            SetupStatus.Text = "音声モデルを起動しています…";
            await LoadRecognizerAsync(_lifetime.Token);
            SetupProgress.Value = 100;
            SetupStatus.Text = string.Empty;
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
        await Task.Run(() => _openCode.StartAsync(_lifetime.Token), _lifetime.Token);
    }

    private async void AiModeToggle_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var enableAiMode = AiModeToggle.IsChecked == true;
        if (_asr is null || _isBusy || _recorder.IsRecording)
        {
            AiModeToggle.IsChecked = _settings.AiModeEnabled;
            RuntimeStatus.Text = _recorder.IsRecording
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
        RuntimeStatus.Text = "選択したAIサービスを確認しています…";
        try
        {
            CaptureCurrentProviderSettings(_settings.AiProviderId);
            ValidateCurrentProviderSettings();
            _settingsStore.Save(_settings);
            if (AiProviderCatalog.UsesOpenCode(_settings.AiProviderId))
            {
                await EnsureOpenCodeReadyAsync();
                await _openCode.EnsureZenFreeModelAvailableAsync(
                    GetProviderSettings(_settings.AiProviderId).ModelId, _lifetime.Token);
            }
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
            ? "オン: 選択したAIサービスで文章を整えます。"
            : "オフ: 文字起こしをそのまま貼り付けます。";
        var providerName = GetProviderDisplayName(_settings.AiProviderId);
        PrivacyNotice.Text = _settings.AiModeEnabled
            ? $"AIモードでは文字起こしテキストを{providerName}へ送信します。音声はPC内で処理します。"
            : "音声と文字起こしはPC内で処理します。AIモードを使うときだけ文字起こしを選択中のサービスへ送信します。";
    }

    private void NormalizeAiSettings()
    {
        _settings.AiProviders ??= new Dictionary<string, AiProviderSettings>(StringComparer.OrdinalIgnoreCase);
        if (!AiProviderCatalog.Options.Any(option => option.Id == _settings.AiProviderId))
            _settings.AiProviderId = AiProviderCatalog.OpenCodeZenFree;
        _ = GetProviderSettings(_settings.AiProviderId);
    }

    private void InitializeAiProviderUi()
    {
        _loadingAiProviderUi = true;
        AiProviderCombo.ItemsSource = AiProviderCatalog.Options;
        AiProviderCombo.DisplayMemberPath = nameof(AiProviderOption.DisplayName);
        AiProviderCombo.SelectedValuePath = nameof(AiProviderOption.Id);
        LoadAiProviderControls(_settings.AiProviderId);
        _loadingAiProviderUi = false;
    }

    private AiProviderSettings GetProviderSettings(string providerId)
    {
        _settings.AiProviders ??= new Dictionary<string, AiProviderSettings>(StringComparer.OrdinalIgnoreCase);
        if (!_settings.AiProviders.TryGetValue(providerId, out var settings) || settings is null)
        {
            settings = AiProviderCatalog.CreateDefaultSettings(providerId);
            _settings.AiProviders[providerId] = settings;
        }
        return settings;
    }

    private void LoadAiProviderControls(string providerId)
    {
        _loadingAiProviderUi = true;
        AiProviderCombo.SelectedValue = providerId;
        var settings = GetProviderSettings(providerId);
        var usesOpenCode = AiProviderCatalog.UsesOpenCode(providerId);
        ZenProviderPanel.Visibility = usesOpenCode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ApiProviderPanel.Visibility = usesOpenCode ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        CompatibleEndpointPanel.Visibility = providerId == AiProviderCatalog.OpenAiCompatible
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        if (usesOpenCode)
        {
            ZenModelCombo.ItemsSource = _zenModelOptions;
            var selectedModel = _zenModelOptions.FirstOrDefault(option => option.ModelId == settings.ModelId);
            ZenModelCombo.SelectedItem = selectedModel;
            if (selectedModel is null) ZenModelCombo.Text = settings.ModelId;
            else ZenModelCombo.Text = selectedModel.DisplayName;
            AiProviderDescription.Text = "既存のOpenCode設定を使います。アプリにAPIキーを入力する必要はありません。";
        }
        else
        {
            ApiModelTextBox.Text = settings.ModelId;
            ApiBaseUrlTextBox.Text = settings.BaseUrl;
            ApiKeyPasswordBox.Clear();
            ApiKeyStatus.Text = string.IsNullOrWhiteSpace(settings.ProtectedApiKey)
                ? "APIキーを入力して保存してください。"
                : "APIキーを保存済みです。新しいキーを入力すると置き換えます。";
            AiProviderDescription.Text = providerId == AiProviderCatalog.OpenAiCompatible
                ? "APIキー、モデルID、OpenAI互換APIのベースURLを指定してください。"
                : "APIキーとモデルIDを指定してください。キーはWindowsのユーザー保護領域に保存します。";
        }
        _loadingAiProviderUi = false;
    }

    private void CaptureCurrentProviderSettings(string providerId)
    {
        var settings = GetProviderSettings(providerId);
        if (AiProviderCatalog.UsesOpenCode(providerId))
        {
            settings.ModelId = GetSelectedZenModelId();
            return;
        }

        settings.ModelId = ApiModelTextBox.Text.Trim();
        if (providerId == AiProviderCatalog.OpenAiCompatible)
            settings.BaseUrl = ApiBaseUrlTextBox.Text.Trim();

        var newApiKey = ApiKeyPasswordBox.Password;
        if (!string.IsNullOrWhiteSpace(newApiKey))
            settings.ProtectedApiKey = CredentialProtector.Protect(newApiKey);
        ApiKeyPasswordBox.Clear();
    }

    private string GetSelectedZenModelId()
    {
        var text = ZenModelCombo.Text.Trim();
        if (ZenModelCombo.SelectedItem is ZenModelOption option &&
            (text == option.DisplayName || text == option.ModelId))
            return option.ModelId;
        return text;
    }

    private void ValidateCurrentProviderSettings(bool requireApiKey = true)
    {
        var settings = GetProviderSettings(_settings.AiProviderId);
        if (string.IsNullOrWhiteSpace(settings.ModelId))
            throw new InvalidOperationException("モデルIDを入力してください。");

        if (AiProviderCatalog.UsesOpenCode(_settings.AiProviderId)) return;

        if (_settings.AiProviderId == AiProviderCatalog.OpenAiCompatible &&
            (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) ||
             (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
             !string.IsNullOrEmpty(baseUri.UserInfo) ||
             (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)))
            throw new InvalidOperationException("HTTPSのベースURLを入力してください。HTTPはlocalhostなどのローカル接続だけ利用できます。URLにユーザー名やパスワードは含めないでください。");

        if (!requireApiKey) return;
        if (string.IsNullOrWhiteSpace(settings.ProtectedApiKey))
            throw new InvalidOperationException("APIキーを入力して保存してください。");
        if (string.IsNullOrWhiteSpace(CredentialProtector.Unprotect(settings.ProtectedApiKey)))
            throw new InvalidOperationException("APIキーを入力して保存してください。");
    }

    private string GetProviderDisplayName(string providerId) =>
        AiProviderCatalog.Options.FirstOrDefault(option => option.Id == providerId)?.DisplayName ?? "AIサービス";

    private void AiProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingAiProviderUi || AiProviderCombo.SelectedValue is not string newProviderId ||
            newProviderId == _settings.AiProviderId) return;

        if (_settings.AiModeEnabled || _isBusy || _recorder.IsRecording)
        {
            LoadAiProviderControls(_settings.AiProviderId);
            RuntimeStatus.Text = _settings.AiModeEnabled
                ? "サービスを変更するには、先にAIモードをオフにしてください。"
                : "録音または処理が終わってからサービスを変更してください。";
            return;
        }

        var oldProviderId = _settings.AiProviderId;
        try
        {
            CaptureCurrentProviderSettings(oldProviderId);
            _settings.AiProviderId = newProviderId;
            _ = GetProviderSettings(newProviderId);
            _settingsStore.Save(_settings);
            if (!AiProviderCatalog.UsesOpenCode(newProviderId)) _openCode.Stop();
            LoadAiProviderControls(newProviderId);
            UpdateAiModeDisplay();
            RuntimeStatus.Text = "AIサービスを切り替えました。設定を確認して保存してください。";
        }
        catch (Exception ex)
        {
            _settings.AiProviderId = oldProviderId;
            LoadAiProviderControls(oldProviderId);
            RuntimeStatus.Text = "AIサービスを切り替えられませんでした。 " + ex.Message;
        }
    }

    private void SaveAiProviderSettings_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings.AiModeEnabled)
        {
            RuntimeStatus.Text = "設定を変更するには、先にAIモードをオフにしてください。";
            return;
        }
        if (_isBusy || _recorder.IsRecording)
        {
            RuntimeStatus.Text = "録音または処理が終わってから設定してください。";
            return;
        }

        try
        {
            CaptureCurrentProviderSettings(_settings.AiProviderId);
            ValidateCurrentProviderSettings(requireApiKey: false);
            _settingsStore.Save(_settings);
            LoadAiProviderControls(_settings.AiProviderId);
            RuntimeStatus.Text = "AIサービスとモデルの設定を保存しました。";
        }
        catch (Exception ex)
        {
            RuntimeStatus.Text = "モデル設定を保存できませんでした。 " + ex.Message;
        }
    }

    private async void RefreshZenModels_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings.AiModeEnabled || _isBusy || _recorder.IsRecording)
        {
            ZenModelStatus.Text = "モデル一覧を更新するには、AIモードをオフにしてください。";
            return;
        }

        var wasRunning = _openCode.IsRunning;
        RefreshZenModelsButton.IsEnabled = false;
        ZenModelStatus.Text = "既存のOpenCodeからモデル一覧を読み込んでいます…";
        var selectedModelId = GetSelectedZenModelId();
        try
        {
            await EnsureOpenCodeReadyAsync();
            _zenModelOptions = await _openCode.GetZenFreeModelsAsync(_lifetime.Token);
            ZenModelCombo.ItemsSource = _zenModelOptions;
            var selected = _zenModelOptions.FirstOrDefault(option => option.ModelId == selectedModelId);
            ZenModelCombo.SelectedItem = selected;
            ZenModelCombo.Text = selected?.DisplayName ?? selectedModelId;
            ZenModelStatus.Text = _zenModelOptions.Count == 0
                ? "OpenCode Zen Freeのモデルが見つかりませんでした。OpenCodeの接続状態を確認してください。"
                : $"{_zenModelOptions.Count}件の無料モデルを読み込みました。";
        }
        catch (Exception ex)
        {
            ZenModelStatus.Text = "モデル一覧を読み込めませんでした。 " + ex.Message;
        }
        finally
        {
            if (!wasRunning && !_settings.AiModeEnabled) _openCode.Stop();
            RefreshZenModelsButton.IsEnabled = true;
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
        BodyText.Text = "入力欄にカーソルを置き、ショートカットを押して話してください。\nもう一度押すと変換して貼り付けます。";
        CapacityText.Visibility = System.Windows.Visibility.Collapsed;
        ShortcutSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        AiProviderSettingsPanel.Visibility = System.Windows.Visibility.Visible;
        ShortcutCaptureStatus.Text = string.Empty;
        UpdateShortcutDisplay();
        UpdateAiModeDisplay();
        PrivacyNotice.Visibility = System.Windows.Visibility.Visible;
        ActionButton.Visibility = System.Windows.Visibility.Collapsed;
        RuntimeStatus.Text = _hotkeyRegistered ? "準備完了" : "ショートカットが登録されていません。別のキーを設定してください。";
    }

    private async Task RecordButton_Click()
    {
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
                _recorder.Start();
                ActionButton.Content = "録音を停止して変換";
                RuntimeStatus.Text = "録音中 · もう一度ショートカットで停止";
                _indicator.ShowStatus("録音中");
            }
            catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
            return;
        }

        await StopAndProcessAsync();
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
            if (string.IsNullOrWhiteSpace(transcript))
                throw new InvalidOperationException("音声を認識できませんでした。マイクに近づいて、もう一度お試しください。");

            string text;
            string resultStatus;
            _indicator.HideStatus();
            if (!_settings.AiModeEnabled)
            {
                text = TranscriptFormatter.NormalizeTranscript(transcript);
                resultStatus = "文字起こしを入力欄へ貼り付けました。";
            }
            else if (!EnsureAiDataConsent())
            {
                text = TranscriptFormatter.NormalizeTranscript(transcript);
                resultStatus = "送信をキャンセルしたため、文字起こしをそのまま貼り付けました。";
            }
            else
            {
                RuntimeStatus.Text = "AIサービスを準備しています…";
                _indicator.ShowStatus("整形中");
                try
                {
                    RuntimeStatus.Text = "AIサービスで文章を整えています…";
                    var providerSettings = GetProviderSettings(_settings.AiProviderId);
                    string response;
                    if (AiProviderCatalog.UsesOpenCode(_settings.AiProviderId))
                    {
                        if (!_openCode.IsRunning) await EnsureOpenCodeReadyAsync();
                        response = await _openCode.RewriteAsync(transcript, providerSettings.ModelId, _lifetime.Token);
                    }
                    else
                    {
                        var apiKey = CredentialProtector.Unprotect(providerSettings.ProtectedApiKey);
                        response = await _aiModelService.RewriteAsync(
                            _settings.AiProviderId, providerSettings, apiKey, transcript, _lifetime.Token);
                    }
                    text = response.Trim();
                    resultStatus = "整えた文章を入力欄へ貼り付けました。";
                }
                catch (OperationCanceledException) when (_closing) { throw; }
                catch (Exception ex)
                {
                    text = TranscriptFormatter.NormalizeTranscript(transcript);
                    resultStatus = "AI変換に失敗したため文字起こしを貼り付けました。 " + ex.Message;
                }
            }

            PasteAndShow(text, resultStatus);
            _indicator.ShowStatus("完了", TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (_closing) { _indicator.HideStatus(); }
        catch (Exception ex)
        {
            RuntimeStatus.Text = ex.Message;
            _indicator.ShowStatus("処理に失敗しました", TimeSpan.FromSeconds(1));
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

    private bool EnsureAiDataConsent()
    {
        var providerSettings = GetProviderSettings(_settings.AiProviderId);
        var termsKey = $"{_settings.AiProviderId}|{providerSettings.ModelId}|{providerSettings.BaseUrl}";
        if (string.Equals(_settings.AiTermsAcceptedFor, termsKey, StringComparison.Ordinal)) return true;

        var providerSpecificTerms = AiProviderCatalog.UsesOpenCode(_settings.AiProviderId) &&
                                    providerSettings.ModelId == "muse-spark-1.3-contributor-free"
            ? "\n\n現在の無料モデルでは、入力と回答がMetaの今後のモデル学習に利用される条件です。"
            : string.Empty;

        var result = System.Windows.MessageBox.Show(this,
            "音声はPC内で処理します。文字起こしテキストは選択したAIサービスへ送信し、生成文を受信して入力欄へ貼り付けます。サービスの利用規約とデータ利用条件を確認してください。" +
            providerSpecificTerms + "\n\n送信を続けますか?",
            "データ送信の確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
        if (result != System.Windows.MessageBoxResult.Yes) return false;

        _settings.AiTermsAcceptedFor = termsKey;
        _settingsStore.Save(_settings);
        return true;
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
            Text = "Auto Prompt",
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
                "Auto Prompt",
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
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _hotkey.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _indicator.Close();
        _recorder.Dispose();
        _asr?.Dispose();
        _openCode.Dispose();
        _aiModelService.Dispose();
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
