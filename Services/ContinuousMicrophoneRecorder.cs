using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class ContinuousMicrophoneRecorder : IDisposable
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = sizeof(short);
    private const int PreRollChunkCount = 3;
    private const int MinimumUtteranceSamples = 2400;
    private const int MaximumQueuedUtterances = 8;
    private const float MinimumVoiceRms = 0.0055f;
    private const float NoiseMultiplier = 2.5f;
    private static readonly TimeSpan EndOfUtteranceSilence = TimeSpan.FromMilliseconds(850);

    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private WaveInEvent? _waveIn;
    private MemoryStream? _speechAudio;
    private Channel<float[]>? _utterances;
    private TaskCompletionSource? _stopped;
    private Task? _stopTask;
    private bool _isSpeaking;
    private float _noiseFloor = 0.0015f;
    private long _lastVoiceTimestamp;
    private Exception? _recordingError;

    public bool IsRecording => _waveIn is not null;

    public void Start(int deviceNumber = -1)
    {
        if (_waveIn is not null)
            throw new InvalidOperationException("録音はすでに開始しています。");
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException("マイクが見つかりません。マイクを接続してからやり直してください。");
        if (deviceNumber < -1 || deviceNumber >= WaveInEvent.DeviceCount)
            throw new InvalidOperationException("選択したマイクが見つかりません。一覧を更新するか、システム設定のマイクを選んでください。");

        lock (_sync)
        {
            ClearQueuedSamplesLocked();
            ClearBuffersLocked();
            _utterances = Channel.CreateBounded<float[]>(new BoundedChannelOptions(MaximumQueuedUtterances)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _speechAudio = new MemoryStream();
            _isSpeaking = false;
            _noiseFloor = 0.0015f;
            _lastVoiceTimestamp = 0;
            _recordingError = null;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = null;
        }

        var recorder = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        _waveIn = recorder;
        try
        {
            recorder.StartRecording();
        }
        catch
        {
            _waveIn = null;
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            lock (_sync)
            {
                _utterances?.Writer.TryComplete();
                _utterances = null;
                ClearBuffersLocked();
            }
            throw;
        }
    }

    public async IAsyncEnumerable<float[]> ReadUtterancesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = _utterances ?? throw new InvalidOperationException("連続録音が開始していません。");
        await foreach (var samples in channel.Reader.ReadAllAsync(cancellationToken))
            yield return samples;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_waveIn is null) return Task.CompletedTask;
            if (_stopTask is not null) return _stopTask.WaitAsync(cancellationToken);
            _stopTask = StopCoreAsync(_waveIn, cancellationToken);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(WaveInEvent recorder, CancellationToken cancellationToken)
    {
        try
        {
            recorder.StopRecording();
            await (_stopped?.Task ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            lock (_sync)
            {
                _waveIn = null;
                ClearQueuedSamplesLocked();
                _utterances?.Writer.TryComplete();
                ClearBuffersLocked();
            }
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();

        lock (_sync)
        {
            _waveIn = null;
            if (_isSpeaking)
                FinalizeUtteranceLocked();
            _isSpeaking = false;
            ClearPreRollLocked();
            _utterances?.Writer.TryComplete(_recordingError);
        }
    }

    public void DiscardPendingUtterances()
    {
        lock (_sync)
            ClearQueuedSamplesLocked();
    }

    public void Dispose()
    {
        var recorder = _waveIn;
        _waveIn = null;
        if (recorder is not null)
        {
            try { recorder.StopRecording(); } catch { }
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
        }

        lock (_sync)
        {
            _utterances?.Writer.TryComplete();
            ClearQueuedSamplesLocked();
            _utterances = null;
            ClearBuffersLocked();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var bytesRecorded = e.BytesRecorded - (e.BytesRecorded % BytesPerSample);
            if (bytesRecorded <= 0) return;
            var chunk = e.Buffer.AsSpan(0, bytesRecorded).ToArray();
            lock (_sync)
                ProcessChunkLocked(chunk);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _recordingError = ex;
                _utterances?.Writer.TryComplete(ex);
            }
        }
    }

    private void ProcessChunkLocked(byte[] chunk)
    {
        if (_speechAudio is null || _utterances is null)
        {
            CryptographicOperations.ZeroMemory(chunk);
            return;
        }

        var rms = CalculateRms(chunk);
        var threshold = Math.Max(MinimumVoiceRms, _noiseFloor * NoiseMultiplier);
        var hasVoice = rms >= threshold;
        if (!_isSpeaking && !hasVoice)
        {
            // Track ordinary room noise slowly so the threshold follows the mic without
            // adapting to brief speech or loud transients.
            if (rms < 0.04f)
                _noiseFloor = Math.Clamp(_noiseFloor * 0.96f + rms * 0.04f, 0.0005f, 0.02f);
            AddPreRollLocked(chunk);
            return;
        }

        if (!_isSpeaking)
        {
            _isSpeaking = true;
            while (_preRoll.TryDequeue(out var priorChunk))
            {
                _speechAudio.Write(priorChunk, 0, priorChunk.Length);
                CryptographicOperations.ZeroMemory(priorChunk);
            }
        }

        _speechAudio.Write(chunk, 0, chunk.Length);
        if (hasVoice)
        {
            _lastVoiceTimestamp = Stopwatch.GetTimestamp();
        }
        else if (_lastVoiceTimestamp != 0 &&
                 Stopwatch.GetElapsedTime(_lastVoiceTimestamp) >= EndOfUtteranceSilence)
        {
            FinalizeUtteranceLocked();
        }

        CryptographicOperations.ZeroMemory(chunk);
    }

    private void FinalizeUtteranceLocked()
    {
        if (_speechAudio is null || _utterances is null) return;

        var bytes = _speechAudio.ToArray();
        if (_speechAudio.TryGetBuffer(out var audioSegment) && audioSegment.Array is not null)
            CryptographicOperations.ZeroMemory(audioSegment.Array.AsSpan(audioSegment.Offset, audioSegment.Count));
        _speechAudio.SetLength(0);
        _isSpeaking = false;
        _lastVoiceTimestamp = 0;
        var sampleCount = bytes.Length / BytesPerSample;
        if (sampleCount < MinimumUtteranceSamples)
        {
            CryptographicOperations.ZeroMemory(bytes);
            return;
        }

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var pcm = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * BytesPerSample, BytesPerSample));
            samples[i] = pcm / 32768f;
        }
        CryptographicOperations.ZeroMemory(bytes);

        if (!_utterances.Writer.TryWrite(samples))
        {
            Array.Clear(samples);
            _recordingError = new InvalidOperationException("音声処理が追いつかず、発話の待ち行列がいっぱいになりました。編集セッションを終了してください。");
            _utterances.Writer.TryComplete(_recordingError);
        }
    }

    private static float CalculateRms(ReadOnlySpan<byte> bytes)
    {
        var sampleCount = bytes.Length / BytesPerSample;
        if (sampleCount == 0) return 0;
        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * BytesPerSample, BytesPerSample)) / 32768.0;
            sumSquares += sample * sample;
        }
        return (float)Math.Sqrt(sumSquares / sampleCount);
    }

    private void AddPreRollLocked(byte[] chunk)
    {
        _preRoll.Enqueue(chunk);
        while (_preRoll.Count > PreRollChunkCount)
        {
            var old = _preRoll.Dequeue();
            CryptographicOperations.ZeroMemory(old);
        }
    }

    private void ClearPreRollLocked()
    {
        while (_preRoll.TryDequeue(out var chunk))
            CryptographicOperations.ZeroMemory(chunk);
    }

    private void ClearQueuedSamplesLocked()
    {
        if (_utterances is null) return;
        while (_utterances.Reader.TryRead(out var samples))
            Array.Clear(samples);
    }

    private void ClearBuffersLocked()
    {
        ClearPreRollLocked();
        if (_speechAudio is not null)
        {
            if (_speechAudio.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            _speechAudio.Dispose();
            _speechAudio = null;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lock (_sync)
                _recordingError = e.Exception;
        }
        _stopped?.TrySetResult();
    }
}
