using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.WebView2.Core;

namespace BetterFlow.Services;

internal static class WebView2RuntimeSetup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (IsAvailable()) return;

            AppPaths.EnsureDirectories();
            var installerPath = Path.Combine(AppPaths.DownloadDirectory, "MicrosoftEdgeWebView2Setup.exe");
            using (var response = await Http.GetAsync(BootstrapperUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 20_000_000)
                    throw new InvalidDataException("WebView2セットアップファイルのサイズが想定範囲を超えています。");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(installerPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, cancellationToken);
            }
            File.Move(installerPath + ".tmp", installerPath, overwrite: true);
            VerifyMicrosoftSignature(installerPath);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/silent /install",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("WebView2 Runtimeのセットアップを開始できませんでした。");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !IsAvailable())
                throw new InvalidOperationException("WebView2 Runtimeを準備できませんでした。");
        }
        finally { Gate.Release(); }
    }

    private static bool IsAvailable()
    {
        try { return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString(null)); }
        catch { return false; }
    }

    private static void VerifyMicrosoftSignature(string path)
    {
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (!certificate.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WebView2 Runtimeインストーラーの署名元がMicrosoftではありません。");
        using var chain = new X509Chain();
        if (!chain.Build(certificate))
            throw new InvalidDataException("WebView2 Runtimeインストーラーの署名を確認できませんでした。");
    }
}
