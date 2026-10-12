using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoPrompt.Services;

internal sealed class OpenCodeInstaller
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/anomalyco/opencode/releases/latest";
    private const string AssetName = "opencode-windows-x64-baseline.zip";
    private const long MinimumArchiveSize = 20_000_000;
    private const long MaximumArchiveSize = 300_000_000;
    private const long MinimumExecutableSize = 10_000_000;
    private const long MaximumExecutableSize = 250_000_000;
    private static readonly TimeSpan SetupLockTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Client = CreateClient();

    public async Task EnsureInstalledAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        AppPaths.EnsureDirectories();
        await using var setupLock = await AcquireSetupLockAsync(cancellationToken);

        if (OpenCodeService.IsCommandAvailable())
        {
            progress?.Report(new SetupProgress("既存のOpenCodeを確認しました。", 100));
            return;
        }

        if (IsUsableExecutable(AppPaths.OpenCodeExecutable))
        {
            progress?.Report(new SetupProgress("OpenCodeの準備ができています。", 100));
            return;
        }

        progress?.Report(new SetupProgress("OpenCodeの公式リリースを確認しています…", 0));
        var release = await GetLatestReleaseAsync(cancellationToken);
        var setupDirectory = Path.Combine(AppPaths.TemporaryDirectory, "opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(setupDirectory);
        try
        {
            var archivePath = Path.Combine(setupDirectory, AssetName);
            progress?.Report(new SetupProgress($"OpenCode {release.Version} をダウンロードしています…", 1));
            await DownloadVerifiedAsync(release, archivePath,
                value => progress?.Report(new SetupProgress($"OpenCode {release.Version} をダウンロードしています…", value)),
                cancellationToken);

            var stagedExecutable = Path.Combine(setupDirectory, "opencode.exe");
            progress?.Report(new SetupProgress("OpenCodeをインストールしています…", 98));
            await ExtractExecutableAsync(archivePath, stagedExecutable, cancellationToken);
            Directory.CreateDirectory(AppPaths.OpenCodeDirectory);
            await MoveFileWithRetryAsync(stagedExecutable, AppPaths.OpenCodeExecutable, cancellationToken);

            if (!IsUsableExecutable(AppPaths.OpenCodeExecutable))
                throw new InvalidDataException("OpenCodeの実行ファイルを確認できませんでした。");

            progress?.Report(new SetupProgress($"OpenCode {release.Version} を準備しました。", 100));
        }
        finally
        {
            TryDeleteDirectory(setupDirectory);
        }
    }

    public static bool IsUsableExecutable(string path) =>
        File.Exists(path) && new FileInfo(path).Length is >= MinimumExecutableSize and <= MaximumExecutableSize;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoPrompt", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<OpenCodeRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await Client.GetAsync(ReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        var root = document.RootElement;
        var version = GetRequiredString(root, "tag_name");
        if (!version.StartsWith('v') || version.Length < 2 || version.Length > 40 ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '+' and not 'v'))
            throw new InvalidDataException("OpenCodeのリリース番号を確認できませんでした。");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenCodeのWindows版配布ファイルが見つかりません。");

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != AssetName) continue;

            var urlText = GetRequiredString(asset, "browser_download_url");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !url.AbsolutePath.StartsWith("/anomalyco/opencode/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("OpenCodeの公式ダウンロードURLを確認できませんでした。");

            if (!asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) ||
                size is < MinimumArchiveSize or > MaximumArchiveSize)
                throw new InvalidDataException("OpenCodeの配布ファイルサイズが想定範囲外です。");

            var digestText = GetRequiredString(asset, "digest");
            if (!digestText.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OpenCode公式配布ファイルのSHA-256を取得できませんでした。");
            var digest = digestText["sha256:".Length..].ToLowerInvariant();
            if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("OpenCode公式配布ファイルのSHA-256形式が正しくありません。");

            return new OpenCodeRelease(version, url, size, digest);
        }

        throw new InvalidDataException("OpenCodeの最新安定版にWindows x64配布ファイルがありません。");
    }

    private static async Task DownloadVerifiedAsync(
        OpenCodeRelease release,
        string destination,
        Action<double> report,
        CancellationToken cancellationToken)
    {
        EnsureFreeSpace(release.Size + MaximumExecutableSize + 64_000_000);
        var partial = destination + ".download";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    total += read;
                    if (total > release.Size)
                        throw new InvalidDataException("OpenCodeのダウンロードサイズが想定値を超えました。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    report(Math.Clamp((double)total / release.Size * 96, 1, 96));
                }
                await output.FlushAsync(timeout.Token);
                if (total != release.Size)
                    throw new InvalidDataException($"OpenCodeのダウンロードサイズが一致しません。 ({total:N0}/{release.Size:N0} bytes)");
            }

            string actualHash;
            await using (var verify = File.OpenRead(partial))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(release.Sha256)))
                throw new InvalidDataException("OpenCode公式配布ファイルのSHA-256が一致しません。");

            File.Move(partial, destination);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
    }

    private static async Task ExtractExecutableAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var executableEntries = archive.Entries
            .Where(entry => Path.GetFileName(entry.FullName.Replace('\\', '/')).Equals("opencode.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (executableEntries.Length != 1 ||
            executableEntries[0].Length is < MinimumExecutableSize or > MaximumExecutableSize)
            throw new InvalidDataException("OpenCodeの配布ファイルから正常な実行ファイルを確認できませんでした。");

        cancellationToken.ThrowIfCancellationRequested();
        await using var input = executableEntries[0].Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new InvalidDataException($"OpenCodeのリリース情報に {propertyName} がありません。");
    }

    private static void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(AppPaths.Root) ?? throw new IOException("OpenCodeの保存先ドライブを特定できません。");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"OpenCodeのセットアップに必要な空き容量が不足しています。最大約 {requiredBytes / 1_000_000_000d:F1} GBが必要です。 (現在 {drive.AvailableFreeSpace / 1_000_000_000d:F1} GB)");
    }

    private static async Task<FileStream> AcquireSetupLockAsync(CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(AppPaths.OpenCodeSetupLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                if (elapsed.Elapsed >= SetupLockTimeout)
                    throw new IOException("別のAuto PromptがOpenCodeをセットアップ中です。完了後に再試行してください。", exception);
                await Task.Delay(300, cancellationToken);
            }
        }
    }

    private static async Task MoveFileWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(8)) throw;
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record OpenCodeRelease(string Version, Uri DownloadUrl, long Size, string Sha256);
}
