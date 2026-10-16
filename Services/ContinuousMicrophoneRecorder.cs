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
    private const int VadFrameBytes = SileroVoiceActivityDetector.WindowBytes;
    private const int PreRollFrameCount = 16;
    private const int MinimumConsecutiveSpeechFrames = 8;
    private const int MinimumUtteranceSamples = 2400;
    private const int MaximumQueuedUtterances = 8;
    private static readonly TimeSpan EndOfUtteranceSilence = TimeSpan.FromMilliseconds(700);

    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly byte[] _vadFrameBuffer = new byte[VadFrameBytes];
    private WaveInEvent? _waveIn;
    private SileroVoiceActivityDetector? _vad;
    private MemoryStream? _speechAudio;
    private Channel<float[]>? _utterances;
    private TaskCompletionSource? _stopped;
    private Task? _stopTask;
    private bool _isSpeaking;
    private bool _acceptUtterances = true;
    private int _minimumConsecutiveSpeechFrames = MinimumConsecutiveSpeechFrames;
    private int _vadFrameBufferedBytes;
    private int _consecutiveSpeechFrames;
    private long _lastVoiceTimestamp;
    private Exception? _recordingError;

    public event Action<float?>? AudioLevelChanged;

    public bool IsRecording => _waveIn is not null;

    public void Start(int deviceNumber = -1)
        => StartCore(deviceNumber, MinimumConsecutiveSpeechFrames);

    private void StartCore(int deviceNumber, int minimumConsecutiveSpeechFrames)
    {
        if (_waveIn is not null)
            throw new InvalidOperationException("録音はすでに開始しています。");
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException("マイクが見つかりません。マイクを接続してからやり直してください。");
        if (deviceNumber < -1 || deviceNumber >= WaveInEvent.DeviceCount)
            throw new InvalidOperationException("選択したマイクが見つかりません。一覧を更新するか、システム設定のマイクを選んでください。");

        var vad = new SileroVoiceActivityDetector();
        lock (_sync)
        {
            ClearQueuedSamplesLocked();
            ClearBuffersLocked();
            _acceptUtterances = true;
            _minimumConsecutiveSpeechFrames = minimumConsecutiveSpeechFrames;
            _vad = vad;
            _utterances = Channel.CreateBounded<float[]>(new BoundedChannelOptions(MaximumQueuedUtterances)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _speechAudio = new MemoryStream();
            _isSpeaking = false;
            _consecutiveSpeechFrames = 0;
            _lastVoiceTimestamp = 0;
            _recordingError = null;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = null;
        }
        PublishAudioLevel(0);

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
                _vad?.Dispose();
                _vad = null;
            }
            PublishAudioLevel(null);
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
                _vad?.Dispose();
                _vad = null;
            }
            PublishAudioLevel(null);
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();

        SileroVoiceActivityDetector? vad;
        lock (_sync)
        {
            _waveIn = null;
            if (_isSpeaking)
                FinalizeUtteranceLocked();
            _isSpeaking = false;
            _consecutiveSpeechFrames = 0;
            ClearPreRollLocked();
            _utterances?.Writer.TryComplete(_recordingError);
            vad = _vad;
            _vad = null;
        }
        vad?.Dispose();
        PublishAudioLevel(null);
    }

    public void DiscardPendingUtterances()
    {
        lock (_sync)
            ClearQueuedSamplesLocked();
    }

    public void SetAcceptingUtterances(bool accept)
    {
        lock (_sync)
        {
            if (_utterances is null || _acceptUtterances == accept)
                return;

            _acceptUtterances = accept;
            ClearQueuedSamplesLocked();
            ClearPreRollLocked();
            CryptographicOperations.ZeroMemory(_vadFrameBuffer);
            _vadFrameBufferedBytes = 0;
            if (_speechAudio is not null)
            {
                if (_speechAudio.TryGetBuffer(out var segment) && segment.Array is not null)
                    CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
                _speechAudio.SetLength(0);
            }
            _isSpeaking = false;
            _consecutiveSpeechFrames = 0;
            _lastVoiceTimestamp = 0;
            _vad?.Reset();
        }
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
            _vad?.Dispose();
            _vad = null;
        }
        PublishAudioLevel(null);
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
            PublishAudioLevel(AudioInputLevel.GetNormalizedLevel(e.Buffer.AsSpan(0, bytesRecorded)));
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

    private void PublishAudioLevel(float? level)
    {
        try { AudioLevelChanged?.Invoke(level); }
        catch { }
    }

    private void ProcessChunkLocked(byte[] chunk)
    {
        try
        {
            if (_speechAudio is null || _utterances is null || _vad is null || _recordingError is not null)
                return;
            if (!_acceptUtterances)
                return;

            var sourceOffset = 0;
            while (sourceOffset < chunk.Length)
            {
                var bytesToCopy = Math.Min(VadFrameBytes - _vadFrameBufferedBytes, chunk.Length - sourceOffset);
                Buffer.BlockCopy(chunk, sourceOffset, _vadFrameBuffer, _vadFrameBufferedBytes, bytesToCopy);
                sourceOffset += bytesToCopy;
                _vadFrameBufferedBytes += bytesToCopy;

                if (_vadFrameBufferedBytes == VadFrameBytes)
                {
                    try { ProcessVadFrameLocked(_vadFrameBuffer); }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(_vadFrameBuffer);
                        _vadFrameBufferedBytes = 0;
                    }
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(chunk); }
    }

    private void ProcessVadFrameLocked(byte[] frame)
    {
        if (_speechAudio is null || _utterances is null || _vad is null)
            return;

        var hasVoice = _vad.AcceptPcm16Frame(frame);
        if (!_isSpeaking)
        {
            AddPreRollLocked(frame);
            _consecutiveSpeechFrames = hasVoice ? _consecutiveSpeechFrames + 1 : 0;
            if (_consecutiveSpeechFrames >= _minimumConsecutiveSpeechFrames)
            {
                _isSpeaking = true;
                while (_preRoll.TryDequeue(out var priorFrame))
                {
                    _speechAudio.Write(priorFrame, 0, priorFrame.Length);
                    CryptographicOperations.ZeroMemory(priorFrame);
                }
                _lastVoiceTimestamp = Stopwatch.GetTimestamp();
                _consecutiveSpeechFrames = 0;
            }
            return;
        }

        _speechAudio.Write(frame, 0, frame.Length);
        if (hasVoice)
        {
            _lastVoiceTimestamp = Stopwatch.GetTimestamp();
        }
        else if (_lastVoiceTimestamp != 0 &&
                 Stopwatch.GetElapsedTime(_lastVoiceTimestamp) >= EndOfUtteranceSilence)
        {
            FinalizeUtteranceLocked();
        }
    }

    private void FinalizeUtteranceLocked()
    {
        if (_speechAudio is null || _utterances is null) return;

        var bytes = _speechAudio.ToArray();
        if (_speechAudio.TryGetBuffer(out var audioSegment) && audioSegment.Array is not null)
            CryptographicOperations.ZeroMemory(audioSegment.Array.AsSpan(audioSegment.Offset, audioSegment.Count));
        _speechAudio.SetLength(0);
        _isSpeaking = false;
        _consecutiveSpeechFrames = 0;
        _lastVoiceTimestamp = 0;
        _vad?.Reset();
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

    private void AddPreRollLocked(ReadOnlySpan<byte> frame)
    {
        _preRoll.Enqueue(frame.ToArray());
        while (_preRoll.Count > PreRollFrameCount)
        {
            var oldFrame = _preRoll.Dequeue();
            CryptographicOperations.ZeroMemory(oldFrame);
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
        CryptographicOperations.ZeroMemory(_vadFrameBuffer);
        _vadFrameBufferedBytes = 0;
        _consecutiveSpeechFrames = 0;
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
