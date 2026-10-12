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
    private bool _exitRequested;
    private bool _syncingStartupMenu;

    public MainWindow()
    {
        InitializeComponent();
        if (Bootstrapper.IsModelReady())
        {
            TitleText.Text = "アプリを起動しています…";
            BodyText.Text = "OpenCodeと音声モデルを確認しています。";
            CapacityText.Text = string.Empty;
        }
        _settings = _settingsStore.Load();
        InitializeTrayIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SystemEvents.SessionEnding += SystemEvents_SessionEnding;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource?)System.Windows.PresentationSource.FromVisual(this);
        _source?.AddHook(WindowMessageHook);
        _hotkeyRegistered = _hotkey.Register(new WindowInteropHelper(this).Handle);
    }

    private async void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        if (!Bootstrapper.IsModelReady()) return;

        TitleText.Text = "アプリを起動しています…";
        BodyText.Text = "OpenCodeと音声モデルを確認しています。";
        CapacityText.Text = string.Empty;
        if (StartupRegistration.IsTrayLaunchRequested)
            HideToTray();

        ActionButton.IsEnabled = false;
        SetupStatus.Text = "OpenCodeと音声モデルを確認しています…";
        SetupProgress.Visibility = System.Windows.Visibility.Visible;
        SetupProgress.IsIndeterminate = true;
        var currentStage = "OpenCodeの確認";
        try
        {
            await EnsureOpenCodeReadyAsync();
            currentStage = "音声モデルの起動";
            await LoadRecognizerAsync(_lifetime.Token);
            SetupStatus.Text = string.Empty;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            TitleText.Text = "起動できませんでした。";
            BodyText.Text = "OpenCodeと音声モデルを確認して、Setupから再試行してください。";
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
        SetupStatus.Text = "既存のOpenCodeを確認しています…";
        RuntimeStatus.Text = string.Empty;
        var currentStage = "OpenCodeの確認";
        try
        {
            await EnsureOpenCodeReadyAsync();
            SetupProgress.IsIndeterminate = false;
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

    private async Task LoadRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = await Task.Run(() => AsrService.Load(AppPaths.ModelFile, AppPaths.TokensFile), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _asr?.Dispose();
        _asr = recognizer;
        TitleText.Text = "セットアップが完了しました。";
        BodyText.Text = "入力欄にカーソルを置き、ショートカットを押して話してください。\nもう一度押すと変換して貼り付けます。";
        CapacityText.Text = "Ctrl + Shift + Space";
        PrivacyNotice.Visibility = System.Windows.Visibility.Visible;
        ActionButton.Content = "録音を開始";
        RuntimeStatus.Text = _hotkeyRegistered ? "準備完了" : "ショートカットを登録できません。ボタンから録音できます。";
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
            }
            catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
            return;
        }

        await StopAndProcessAsync();
    }

    private async Task StopAndProcessAsync()
    {
        _isBusy = true;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "音声を文字起こししています…";
        RuntimeStatus.Text = "音声をPC内で文字起こししています…";
        float[]? samples = null;
        try
        {
            samples = await _recorder.StopAsync(_lifetime.Token);
            var transcript = await Task.Run(() => _asr!.Transcribe(samples), _lifetime.Token);
            Array.Clear(samples);
            samples = null;
            if (string.IsNullOrWhiteSpace(transcript))
                throw new InvalidOperationException("音声を認識できませんでした。マイクに近づいて、もう一度お試しください。");

            string prompt;
            string resultStatus;
            if (!EnsureContributorConsent())
            {
                prompt = PromptFormatter.PlainFallback(transcript);
                resultStatus = "送信をキャンセルしたため、文字起こしをそのまま貼り付けました。";
            }
            else if (_openCode.IsRunning)
            {
                RuntimeStatus.Text = "OpenCode Zenで10行以下に整えています…";
                try
                {
                    var response = await _openCode.RewriteAsync(transcript, _lifetime.Token);
                    prompt = PromptFormatter.FitWithinTenLines(response);
                    resultStatus = "プロンプトを入力欄へ貼り付けました。";
                }
                catch (Exception ex)
                {
                    prompt = PromptFormatter.PlainFallback(transcript);
                    resultStatus = "文章変換に失敗したため文字起こしを貼り付けました。 " + ex.Message;
                }
            }
            else
            {
                prompt = PromptFormatter.PlainFallback(transcript);
                resultStatus = "OpenCodeが起動していないため、文字起こしをそのまま貼り付けました。";
            }

            ResultBox.Text = prompt;
            ResultPanel.Visibility = System.Windows.Visibility.Visible;
            PasteAndShow(prompt, resultStatus);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
        finally
        {
            if (samples is not null) Array.Clear(samples);
            _isBusy = false;
            ActionButton.IsEnabled = true;
            ActionButton.Content = "録音を開始";
        }
    }

    private bool EnsureContributorConsent()
    {
        if (_settings.ContributorTermsAccepted) return true;

        var result = System.Windows.MessageBox.Show(this,
            "音声そのものはPC内で処理し、文字起こしテキストを既存のOpenCode設定で接続されたOpenCode Zenへ送信します。生成結果を受信して入力欄へ貼り付けます。\n\nMuse Spark 1.3 Contributor Freeは、プロンプトと回答をMetaの将来モデルの学習に使う条件です。\n\nこの内容を確認し、送信を続けますか?",
            "データ送信の確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
        if (result != System.Windows.MessageBoxResult.Yes) return false;

        _settings.ContributorTermsAccepted = true;
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
            RuntimeStatus.Text = "自動貼り付けに失敗しました。結果欄からコピーしてください。 " + ex.Message;
        }
    }

    private void CopyResultButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        try
        {
            System.Windows.Clipboard.SetText(ResultBox.Text);
            RuntimeStatus.Text = "プロンプトをクリップボードにコピーしました。";
        }
        catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
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
        _recorder.Dispose();
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
