using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using SherpaOnnx;

namespace BetterFlow.Services;

internal sealed class SileroVoiceActivityDetector : IDisposable
{
    public const int SampleRate = 16000;
    public const int WindowSamples = 512;
    public const int WindowBytes = WindowSamples * sizeof(short);

    private const string ModelResourceName = "BetterFlow.Assets.silero_vad.onnx";
    private const string ModelSha256 = "9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6";
    private const float SpeechThreshold = 0.60f;
    private const float MinSilenceDurationSeconds = 0.18f;
    private const float MinSpeechDurationSeconds = 0.12f;
    private const float MaxSpeechDurationSeconds = 120f;

    private readonly VoiceActivityDetector _detector;

    public SileroVoiceActivityDetector()
    {
        var vadModel = new SileroVadModelConfig
        {
            Model = EnsureModelAvailable(),
            Threshold = SpeechThreshold,
            MinSilenceDuration = MinSilenceDurationSeconds,
            MinSpeechDuration = MinSpeechDurationSeconds,
            WindowSize = WindowSamples,
            MaxSpeechDuration = MaxSpeechDurationSeconds
        };
        var config = new VadModelConfig
        {
            SileroVad = vadModel,
            SampleRate = SampleRate,
            NumThreads = 1,
            Provider = "cpu",
            Debug = 0
        };

        _detector = new VoiceActivityDetector(config, MaxSpeechDurationSeconds + 1f);
    }

    public bool AcceptPcm16Frame(ReadOnlySpan<byte> pcmFrame)
    {
        if (pcmFrame.Length != WindowBytes)
            throw new ArgumentException($"Silero VAD needs exactly {WindowBytes} bytes per frame.", nameof(pcmFrame));

        var samples = new float[WindowSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            var pcm = BinaryPrimitives.ReadInt16LittleEndian(pcmFrame.Slice(i * sizeof(short), sizeof(short)));
            samples[i] = pcm / 32768f;
        }

        _detector.AcceptWaveform(samples);
        return _detector.IsSpeechDetected();
    }

    public void Reset() => _detector.Reset();

    public void Dispose() => _detector.Dispose();

    private static string EnsureModelAvailable()
    {
        var modelDirectory = Path.Combine(AppPaths.Root, "models", "silero-vad");
        var modelPath = Path.Combine(modelDirectory, "silero_vad.onnx");
        if (File.Exists(modelPath) && HasExpectedHash(modelPath))
            return modelPath;

        Directory.CreateDirectory(modelDirectory);
        using var modelStream = typeof(SileroVoiceActivityDetector).Assembly.GetManifestResourceStream(ModelResourceName)
            ?? throw new InvalidOperationException("Silero VAD model is missing from this application build.");

        var temporaryPath = modelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                modelStream.CopyTo(output);

            if (!HasExpectedHash(temporaryPath))
                throw new InvalidDataException("The bundled Silero VAD model failed its integrity check.");

            File.Move(temporaryPath, modelPath, overwrite: true);
            return modelPath;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool HasExpectedHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(ModelSha256));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
