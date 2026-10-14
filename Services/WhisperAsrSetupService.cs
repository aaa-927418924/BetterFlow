using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BetterFlow.Services;

internal sealed class WhisperAsrSetupService
{
    private const string Repository = "csukuangfj/sherpa-onnx-whisper-turbo";
    private const string Revision = "main";
    private const string EncoderName = "turbo-encoder.int8.onnx";
    private const string DecoderName = "turbo-decoder.int8.onnx";
    private const string TokensName = "turbo-tokens.txt";
    private const string EncoderSha256 = "b02dcdf54f348741e93fe732b67d933c8dcb6735655f710640143081db38878b";
    private const string DecoderSha256 = "20accd02388482eb3a46bd615631adfdc85e1eb2c7db9ea3f02a40ffe6b81547";
    private const string MarkerContents = "whisper-large-v3-turbo-int8-v1|" + EncoderSha256 + "|" + DecoderSha256;
    private const long MinimumFreeSpaceReserve = 50_000_000;
    private const long EstimatedEncoderDownloadBytes = 800_000_000;
    private const long EstimatedDecoderDownloadBytes = 450_000_000;
    private const long EstimatedTokensDownloadBytes = 2_000_000;
    private const long MaximumEncoderBytes = 800_000_000;
    private const long MaximumDecoderBytes = 450_000_000;
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool IsReady()
    {
        if (!File.Exists(AppPaths.WhisperModelMarkerFile) ||
            !File.Exists(AppPaths.WhisperEncoderFile) ||
            !File.Exists(AppPaths.WhisperDecoderFile) ||
            !File.Exists(AppPaths.WhisperTokensFile))
            return false;

        try
        {
            return File.ReadAllText(AppPaths.WhisperModelMarkerFile) == MarkerContents &&
                   new FileInfo(AppPaths.WhisperEncoderFile).Length > 600_000_000 &&
                   new FileInfo(AppPaths.WhisperDecoderFile).Length > 300_000_000 &&
                   new FileInfo(AppPaths.WhisperTokensFile).Length is > 700_000 and < 1_500_000;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task EnsureReadyAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        if (IsReady())
        {
            progress.Report(new SetupProgress("Whisper Large V3 Turboを確認しました。", 96));
            return;
        }

        Directory.CreateDirectory(AppPaths.WhisperModelDirectory);
        var encoderReady = await IsVerifiedFileAsync(AppPaths.WhisperEncoderFile, EncoderSha256, cancellationToken);
        var decoderReady = await IsVerifiedFileAsync(AppPaths.WhisperDecoderFile, DecoderSha256, cancellationToken);
        var tokensReady = IsTokensFileReady();
        var requiredBytes = MinimumFreeSpaceReserve +
                            (encoderReady ? 0 : EstimatedEncoderDownloadBytes) +
                            (decoderReady ? 0 : EstimatedDecoderDownloadBytes) +
                            (tokensReady ? 0 : EstimatedTokensDownloadBytes);
        EnsureFreeSpace(requiredBytes);
        progress.Report(new SetupProgress("Whisper Large V3 Turboをダウンロードしています…", 1));

        await DownloadVerifiedAsync(EncoderName, AppPaths.WhisperEncoderFile, EncoderSha256,
            MaximumEncoderBytes, encoderReady, progress, 1, 60, cancellationToken);
        await DownloadVerifiedAsync(DecoderName, AppPaths.WhisperDecoderFile, DecoderSha256,
            MaximumDecoderBytes, decoderReady, progress, 61, 34, cancellationToken);
        await DownloadTokensAsync(tokensReady, progress, cancellationToken);

        if (new FileInfo(AppPaths.WhisperTokensFile).Length is < 700_000 or > 1_500_000)
            throw new InvalidDataException("Whisperのtokenファイルのサイズが想定範囲外です。");

        var markerTemporary = AppPaths.WhisperModelMarkerFile + ".tmp";
        await File.WriteAllTextAsync(markerTemporary, MarkerContents, new UTF8Encoding(false), cancellationToken);
        File.Move(markerTemporary, AppPaths.WhisperModelMarkerFile, overwrite: true);
        if (!IsReady())
            throw new InvalidDataException("Whisper Large V3 Turboの必要ファイルを確認できませんでした。");
        progress.Report(new SetupProgress("Whisper Large V3 Turboを確認しました。", 96));
    }

    private static async Task DownloadVerifiedAsync(
        string fileName,
        string destination,
        string expectedSha256,
        long maximumBytes,
        bool alreadyVerified,
        IProgress<SetupProgress> progress,
        double startPercent,
        double progressRange,
        CancellationToken cancellationToken)
    {
        if (alreadyVerified)
        {
            progress.Report(new SetupProgress("Whisper Large V3 Turboを確認しています…", startPercent + progressRange));
            return;
        }

        var partial = destination + ".download";
        TryDelete(partial);
        var url = $"https://huggingface.co/{Repository}/resolve/{Revision}/{fileName}?download=true";
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximumBytes))
                throw new InvalidDataException($"{fileName}のダウンロードサイズが想定範囲外です。");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > maximumBytes)
                        throw new InvalidDataException($"{fileName}のダウンロードサイズが想定範囲を超えました。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var fraction = response.Content.Headers.ContentLength is long expected && expected > 0
                        ? Math.Min(1, (double)total / expected)
                        : 0;
                    progress.Report(new SetupProgress("Whisper Large V3 Turboをダウンロードしています…",
                        startPercent + progressRange * fraction));
                }
                await output.FlushAsync(cancellationToken);
                if (total < 100_000)
                    throw new InvalidDataException($"{fileName}のダウンロードが途中で終了しました。");
            }

            var actualHash = await ComputeSha256Async(partial, cancellationToken);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{fileName}のSHA-256が一致しません。");
            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task DownloadTokensAsync(bool alreadyPresent, IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        if (alreadyPresent)
        {
            progress.Report(new SetupProgress("Whisper Large V3 Turboの構成ファイルを確認しています…", 97));
            return;
        }

        var partial = AppPaths.WhisperTokensFile + ".download";
        TryDelete(partial);
        var url = $"https://huggingface.co/{Repository}/resolve/{Revision}/{TokensName}?download=true";
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             128 * 1024, useAsync: true))
                await input.CopyToAsync(output, cancellationToken);

            if (new FileInfo(partial).Length is < 700_000 or > 1_500_000)
                throw new InvalidDataException("Whisperのtokenファイルのサイズが想定範囲外です。");
            File.Move(partial, AppPaths.WhisperTokensFile, overwrite: true);
            progress.Report(new SetupProgress("Whisper Large V3 Turboの構成ファイルを確認しています…", 97));
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task<bool> IsVerifiedFileAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return string.Equals(await ComputeSha256Async(path, cancellationToken), expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool IsTokensFileReady() => File.Exists(AppPaths.WhisperTokensFile) &&
                                                new FileInfo(AppPaths.WhisperTokensFile).Length is > 700_000 and < 1_500_000;

    private static void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(AppPaths.Root) ?? throw new IOException("アプリの保存先ドライブを特定できません。");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < requiredBytes)
            throw new IOException($"空き容量が不足しています。Whisper Large V3 Turboのモデルを準備するには、追加で約{requiredBytes / 1_000_000_000d:F1} GB必要です。 (現在 {available / 1_000_000_000d:F1} GB)");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
