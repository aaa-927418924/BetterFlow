using System.Diagnostics;
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
    private static readonly TimeSpan SetupLockTimeout = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task EnsureModelAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        AppPaths.EnsureDirectories();
        progress.Report(new SetupProgress("音声モデルのセットアップ状態を確認しています…", 0));
        await using var setupLock = await AcquireSetupLockAsync(cancellationToken);

        if (!IsModelReady())
        {
            EnsureFreeSpace(1_200_000_000);
            var setupDirectory = Path.Combine(AppPaths.TemporaryDirectory, "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(setupDirectory);
            try
            {
                progress.Report(new SetupProgress("日本語音声モデルをダウンロードしています…", 1));
                var archivePath = Path.Combine(setupDirectory, "parakeet-ja-int8.tar.bz2");
                await DownloadVerifiedAsync(ModelUrl, archivePath, ModelArchiveSize, ModelArchiveSha256,
                    value => progress.Report(new SetupProgress("日本語音声モデルをダウンロードしています…", value * 0.75)), cancellationToken);
                await Task.Run(() => ExtractModel(archivePath, cancellationToken, progress), cancellationToken);
                if (!IsModelReady())
                    throw new InvalidDataException("音声モデルの必要ファイルが見つかりませんでした。");
            }
            finally
            {
                TryDeleteDirectory(setupDirectory);
            }
        }
        else
        {
            progress.Report(new SetupProgress("日本語音声モデルを確認しました。", 96));
        }

        progress.Report(new SetupProgress("音声モデルを起動しています…", 98));
    }

    private static async Task<FileStream> AcquireSetupLockAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(AppPaths.ModelSetupLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (elapsed.Elapsed >= SetupLockTimeout)
                    throw new IOException("別のAuto Promptが音声モデルをセットアップ中です。起動中のセットアップが終わるまで待ってから再試行してください。", ex);
                await Task.Delay(300, cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

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

            string actualHash;
            await using (var verify = File.OpenRead(partial))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(expectedSha256)))
                throw new InvalidDataException("ダウンロードしたファイルのSHA-256が一致しません。");

            await MoveFileWithRetryAsync(partial, destination, overwrite: true, cancellationToken);
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
            MoveFileWithRetry(Path.Combine(staging, "model.int8.onnx"), AppPaths.ModelFile, overwrite: true, cancellationToken);
            MoveFileWithRetry(Path.Combine(staging, "tokens.txt"), AppPaths.TokensFile, overwrite: true, cancellationToken);
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

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void MoveFileWithRetry(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(8)) throw;
                Thread.Sleep(250);
            }
        }
    }

    private static async Task MoveFileWithRetryAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(8)) throw;
                await Task.Delay(250, cancellationToken);
            }
        }
    }
}

internal sealed record SetupProgress(string Message, double Percent);
