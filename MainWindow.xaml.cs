using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AutoPrompt.Services;

namespace AutoPrompt;

public partial class MainWindow : System.Windows.Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SecretStore _secretStore = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly MicrophoneRecorder _recorder = new();
    private readonly CancellationTokenSource _lifetime = new();
    private AsrService? _asr;
    private OpenCodeService _openCode = new();
    private AppSettings _settings;
    private HwndSource? _source;
    private IntPtr _pasteTarget;
    private bool _isBusy;
    private bool _closing;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource?)System.Windows.PresentationSource.FromVisual(this);
        _source?.AddHook(WindowMessageHook);
        _hotkeyRegistered = _hotkey.Register(new WindowInteropHelper(this).Handle);
        if (!_hotkeyRegistered)
            RuntimeStatus.Text = "ショートカットを登録できません。ボタンから録音できます。";
    }

    private async void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        OpenCodeStatus.Text = File.Exists(AppPaths.OpenCodeExecutable) ? "取得済み · v1.19.0" : "未取得 · 約60 MB";
        ModelStatus.Text = Bootstrapper.IsModelReady() ? "取得済み · INT8" : "未取得 · ダウンロード約490 MB";
        if (Bootstrapper.IsModelReady())
        {
            SetupStatus.Text = "音声モデルを起動しています…";
            SetupButton.IsEnabled = false;
            try
            {
                await LoadRecognizerAsync(_lifetime.Token);
                SetupProgress.Value = 100;
                SetupStatus.Text = "音声モデルを起動しました。";
                SetupButton.Content = "音声モデルは準備できています";
                SetupButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                SetupStatus.Text = "音声モデルを起動できませんでした。セットアップを再実行してください。";
                SetupButton.Content = "セットアップを再実行";
                SetupButton.IsEnabled = true;
                RuntimeStatus.Text = ex.Message;
            }
        }

        if (_settings.ContributorTermsAccepted && _secretStore.HasKey)
        {
            ZenStatus.Text = "キー保存済み · 未検証";
            await StartOpenCodeAsync(showError: false);
        }
        else if (_secretStore.HasKey)
        {
            ZenStatus.Text = "キー保存済み · 条件の確認が必要";
        }

        RefreshReadyState();
        if (!_hotkeyRegistered)
            RuntimeStatus.Text = "ショートカットを登録できません。別アプリと重複している可能性があります。録音ボタンは使えます。";
    }

    private async void SetupButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        SetupButton.IsEnabled = false;
        SetupProgress.Visibility = System.Windows.Visibility.Visible;
        SetupProgress.Value = 0;
        SetupButton.Content = "セットアップ中…";
        try
        {
            var progress = new Progress<SetupProgress>(update =>
            {
                SetupStatus.Text = update.Message;
                SetupProgress.Value = Math.Clamp(update.Percent, 0, 100);
                OpenCodeStatus.Text = File.Exists(AppPaths.OpenCodeExecutable) ? "取得済み · v1.19.0" : "ダウンロード中…";
                ModelStatus.Text = Bootstrapper.IsModelReady() ? "取得済み · INT8" : update.Percent >= 15 ? "ダウンロード / 展開中…" : "未取得 · ダウンロード約490 MB";
            });
            await new Bootstrapper().EnsureAssetsAsync(progress, _lifetime.Token);
            await LoadRecognizerAsync(_lifetime.Token);
            SetupProgress.Value = 100;
            SetupStatus.Text = "セットアップ完了。音声モデルはこのPCで起動中です。";
            SetupButton.Content = "音声モデルは準備できています";
            ZenPanel.Visibility = System.Windows.Visibility.Visible;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            SetupStatus.Text = "セットアップに失敗しました。ネット接続と空き容量を確認し、もう一度お試しください。";
            RuntimeStatus.Text = ex.Message;
            SetupButton.Content = "セットアップを再実行";
            SetupProgress.Visibility = System.Windows.Visibility.Collapsed;
        }
        finally
        {
            _isBusy = false;
            SetupButton.IsEnabled = _asr is null;
            RefreshReadyState();
        }
    }

    private async Task LoadRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = await Task.Run(() => AsrService.Load(AppPaths.ModelFile, AppPaths.TokensFile), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _asr?.Dispose();
        _asr = recognizer;
        ReadyPanel.Visibility = System.Windows.Visibility.Visible;
        RuntimeStatus.Text = "待機中";
    }

    private async void SaveKeyButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var key = ZenApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ZenStatus.Text = "APIキーを入力してください。";
            ZenApiKeyBox.Focus();
            return;
        }
        if (!ConsentCheck.IsChecked.GetValueOrDefault())
        {
            ZenStatus.Text = "データ利用条件への同意が必要です。";
            ConsentCheck.Focus();
            return;
        }
        if (!File.Exists(AppPaths.OpenCodeExecutable))
        {
            ZenStatus.Text = "先にセットアップを実行してください。";
            return;
        }

        try
        {
            _secretStore.Save(key);
            _settings.ContributorTermsAccepted = true;
            _settingsStore.Save(_settings);
            ZenApiKeyBox.Clear();
            ZenStatus.Text = "キーを保存しました。OpenCode起動中…";
            SaveKeyButton.IsEnabled = false;
            _openCode.Dispose();
            _openCode = new OpenCodeService();
            await StartOpenCodeAsync(showError: true);
        }
        catch (Exception ex)
        {
            ZenStatus.Text = "キーを保存できませんでした。";
            RuntimeStatus.Text = ex.Message;
        }
        finally
        {
            SaveKeyButton.IsEnabled = true;
            RefreshReadyState();
        }
    }

    private async Task StartOpenCodeAsync(bool showError)
    {
        var key = _secretStore.Load();
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            await Task.Run(() => _openCode.StartAsync(key, _lifetime.Token), _lifetime.Token);
            ZenStatus.Text = "キー保存済み · OpenCode起動中（未検証）";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            ZenStatus.Text = "キー保存済み · OpenCode起動エラー";
            RuntimeStatus.Text = ex.Message;
            if (showError)
                System.Windows.MessageBox.Show(this, ex.Message, "OpenCodeを起動できません", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OpenZenButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://opencode.ai/auth") { UseShellExecute = true });
        }
        catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
    }

    private async void RecordButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy || _asr is null)
        {
            RuntimeStatus.Text = _asr is null ? "先にセットアップを完了してください。" : "処理が終わるまでお待ちください。";
            return;
        }

        if (!_recorder.IsRecording)
        {
            try
            {
                _pasteTarget = PasteService.GetForegroundWindowHandle();
                _recorder.Start();
                RecordButton.Content = "録音を停止して変換";
                ReadyBadge.Text = "録音中";
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
        RecordButton.IsEnabled = false;
        RecordButton.Content = "音声を文字起こししています…";
        ReadyBadge.Text = "処理中";
        RuntimeStatus.Text = "音声をローカルで文字起こししています…";
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
            if (_settings.ContributorTermsAccepted && _openCode.IsRunning)
            {
                RuntimeStatus.Text = "OpenCode Zenで10行以下に整えています…";
                try
                {
                    var response = await _openCode.RewriteAsync(transcript, _lifetime.Token);
                    prompt = PromptFormatter.FitWithinTenLines(response);
                }
                catch (Exception ex)
                {
                    prompt = PromptFormatter.PlainFallback(transcript);
                    RuntimeStatus.Text = "文章変換に失敗したため、文字起こしをそのまま貼り付けます。";
                    ResultBox.Text = prompt;
                    ResultPanel.Visibility = System.Windows.Visibility.Visible;
                    PasteAndShow(prompt, "そのままコピーしました");
                    RuntimeStatus.Text += " " + ex.Message;
                    return;
                }
            }
            else
            {
                prompt = PromptFormatter.PlainFallback(transcript);
                RuntimeStatus.Text = "Zen未接続のため、文字起こしをそのまま貼り付けます。";
            }

            ResultBox.Text = prompt;
            ResultPanel.Visibility = System.Windows.Visibility.Visible;
            PasteAndShow(prompt, "プロンプトを入力欄へ貼り付けました。");
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { RuntimeStatus.Text = ex.Message; }
        finally
        {
            if (samples is not null) Array.Clear(samples);
            _isBusy = false;
            RecordButton.IsEnabled = true;
            RecordButton.Content = "録音を開始";
            ReadyBadge.Text = "準備完了";
        }
    }

    private void PasteAndShow(string text, string successStatus)
    {
        try
        {
            var paste = PasteService.CopyAndPaste(text, _pasteTarget);
            RuntimeStatus.Text = paste == PasteResult.Pasted ? successStatus : "プロンプトをコピーしました。貼り付け先が変わったため、Ctrl+Vで入力してください。";
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

    private void RefreshReadyState()
    {
        var ready = _asr is not null;
        ReadyPanel.Visibility = ready ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (SetupButton is not null && ready)
        {
            SetupButton.IsEnabled = false;
            SetupButton.Content = "音声モデルは準備できています";
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotkey.MessageId && _hotkey.IsMessage(wParam.ToInt32()))
        {
            handled = true;
            RecordButton_Click(this, new System.Windows.RoutedEventArgs());
        }
        return IntPtr.Zero;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _lifetime.Cancel();
        _hotkey.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _recorder.Dispose();
        _asr?.Dispose();
        _openCode.Dispose();
        _lifetime.Dispose();
    }
}
