using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SharpCompress.Readers;

namespace AutoPrompt.Services;

internal sealed class Bootstrapper
{
    private const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8.tar.bz2";
    private const long ModelArchiveSize = 489_389_564;
    private const string ModelArchiveSha256 = "4b0a800ef29f4f4c8667339bf6f60d5bfdc2852ddc9dc5741aea65b6f8d1306b";

    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task EnsureModelAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        AppPaths.EnsureDirectories();

        if (!IsModelReady())
        {
            EnsureFreeSpace(1_200_000_000);
            progress.Report(new SetupProgress("日本語音声モデルをダウンロードしています…", 1));
            var archivePath = Path.Combine(AppPaths.DownloadDirectory, "parakeet-ja-int8.tar.bz2");
            await DownloadVerifiedAsync(ModelUrl, archivePath, ModelArchiveSize, ModelArchiveSha256,
                value => progress.Report(new SetupProgress("日本語音声モデルをダウンロードしています…", value * 0.75)), cancellationToken);
            ExtractModel(archivePath, cancellationToken, progress);
            if (!IsModelReady())
                throw new InvalidDataException("音声モデルの必要ファイルが見つかりませんでした。");
            TryDelete(archivePath);
        }
        else
        {
            progress.Report(new SetupProgress("日本語音声モデルを確認しました。", 96));
        }

        progress.Report(new SetupProgress("音声モデルを起動しています…", 98));
    }

    public static bool IsModelReady() =>
        File.Exists(AppPaths.ModelFile) && new FileInfo(AppPaths.ModelFile).Length > 500_000_000 &&
        File.Exists(AppPaths.TokensFile) && new FileInfo(AppPaths.TokensFile).Length > 100;

    private static async Task DownloadVerifiedAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha256,
        Action<double> report,
        CancellationToken cancellationToken)
    {
        var partial = destination + ".download";
        TryDelete(partial);
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > expectedSize)
                        throw new InvalidDataException("ダウンロードサイズが想定値を超えました。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    report(Math.Min(1, (double)total / expectedSize) * 100);
                }
                await output.FlushAsync(cancellationToken);
                if (total != expectedSize)
                    throw new InvalidDataException($"ダウンロードサイズが一致しません。 ({total:N0}/{expectedSize:N0} bytes)");
            }

            await using var verify = File.OpenRead(partial);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(expectedSha256)))
                throw new InvalidDataException("ダウンロードしたファイルのSHA-256が一致しません。");

            File.Move(partial, destination, overwrite: true);
            report(100);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void ExtractModel(string archivePath, CancellationToken cancellationToken, IProgress<SetupProgress> progress)
    {
        var staging = Path.Combine(AppPaths.TemporaryDirectory, "model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var stream = File.OpenRead(archivePath);
            using var reader = ReaderFactory.OpenReader(stream);
            var foundModel = false;
            var foundTokens = false;
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var filename = Path.GetFileName((reader.Entry.Key ?? string.Empty).Replace('\\', '/'));
                if (filename is not ("model.int8.onnx" or "tokens.txt")) continue;
                var destination = filename == "model.int8.onnx"
                    ? Path.Combine(staging, "model.int8.onnx")
                    : Path.Combine(staging, "tokens.txt");
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    reader.WriteEntryTo(output);
                if (filename == "model.int8.onnx") foundModel = true;
                if (filename == "tokens.txt") foundTokens = true;
                progress.Report(new SetupProgress("音声モデルを展開しています…", foundModel && foundTokens ? 96 : 94));
            }

            if (!foundModel || !foundTokens)
                throw new InvalidDataException("音声モデルのアーカイブに必要ファイルがありません。");

            Directory.CreateDirectory(AppPaths.ModelDirectory);
            File.Move(Path.Combine(staging, "model.int8.onnx"), AppPaths.ModelFile, overwrite: true);
            File.Move(Path.Combine(staging, "tokens.txt"), AppPaths.TokensFile, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(AppPaths.Root) ?? throw new IOException("アプリの保存先ドライブを特定できません。");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"空き容量が不足しています。1.2 GB以上の空き領域が必要です。 (現在 {drive.AvailableFreeSpace / 1_000_000_000d:F1} GB)");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal sealed record SetupProgress(string Message, double Percent);
