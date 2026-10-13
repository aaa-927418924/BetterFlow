using SherpaOnnx;

namespace BetterFlow.Services;

internal sealed class AsrService : IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    private AsrService(OfflineRecognizer recognizer)
    {
        _recognizer = recognizer;
    }

    public static AsrService Load(string modelPath, string tokensPath)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.NeMoCtc.Model = modelPath;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new AsrService(new OfflineRecognizer(config));
    }

    public string Transcribe(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        _recognizer.Decode(new[] { stream });
        return stream.Result.Text.Trim();
    }

    public void Dispose() => _recognizer.Dispose();
}
