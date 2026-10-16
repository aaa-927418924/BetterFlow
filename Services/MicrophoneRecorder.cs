using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class MicrophoneRecorder : IDisposable
{
    private static readonly TimeSpan SilenceAutoStopDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SilenceMonitorInterval = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private readonly object _silenceMonitorSync = new();
    private WaveInEvent? _waveIn;
    private List<CaptureSegmentBuffer>? _segments;
    private TaskCompletionSource? _stopped;
    private Exception? _recordingError;
    private bool _voiceCommandCaptureActive;
    private SileroVoiceActivityDetector? _silenceVad;
    private byte[]? _silenceVadFrameBuffer;
    private System.Threading.Timer? _silenceTimer;
    private int _silenceVadFrameBufferedBytes;
    private long _lastSpeechTimestamp;
    private int _silenceTimeoutRaised;

    public event Action<float?>? AudioLevelChanged;
    public event Action? SilenceTimeoutReached;
    public event Action<Exception>? SilenceDetectionFailed;

    public static IReadOnlyList<AudioInputDeviceOption> GetInputDevices()
    {
        var devices = new List<AudioInputDeviceOption>
        {
            new(AudioInputDeviceOption.SystemDefaultId, -1, "システム設定のマイク (既定)")
        };
        var duplicateNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var deviceNumber = 0; deviceNumber < WaveInEvent.DeviceCount; deviceNumber++)
        {
            var capabilities = WaveInEvent.GetCapabilities(deviceNumber);
            var productName = capabilities.ProductName?.Trim();
            if (string.IsNullOrWhiteSpace(productName))
                productName = $"音声入力デバイス {deviceNumber + 1}";

            duplicateNames.TryGetValue(productName, out var occurrence);
            occurrence++;
            duplicateNames[productName] = occurrence;
            var id = capabilities.NameGuid == Guid.Empty
                ? $"wavein:{productName}:{occurrence}"
                : $"wavein:{capabilities.NameGuid:D}:{occurrence}";
            var displayName = occurrence == 1
                ? productName
                : $"{productName} ({occurrence})";
            devices.Add(new AudioInputDeviceOption(id, deviceNumber, displayName));
        }

        return devices;
    }

    public bool IsRecording => _waveIn is not null;

    public void Start(int deviceNumber = -1, bool autoStopAfterSixtySecondsOfSilence = false)
    {
        if (_waveIn is not null)
            throw new InvalidOperationException("録音はすでに開始しています。");
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException("マイクが見つかりません。マイクを接続してからやり直してください。");
        if (deviceNumber < -1 || deviceNumber >= WaveInEvent.DeviceCount)
            throw new InvalidOperationException("選択したマイクが見つかりません。一覧を更新するか、システム設定のマイクを選んでください。");

        ClearAudioBuffer();
        lock (_sync)
        {
            _segments = [new CaptureSegmentBuffer(isVoiceCommand: false)];
            _voiceCommandCaptureActive = false;
        }
        _recordingError = null;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishAudioLevel(0);

        WaveInEvent? recorder = null;
        try
        {
            if (autoStopAfterSixtySecondsOfSilence)
                StartSilenceMonitor();

            recorder = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50
            };
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            _waveIn = recorder;
            recorder.StartRecording();
        }
        catch
        {
            _waveIn = null;
            if (recorder is not null)
            {
                recorder.DataAvailable -= OnDataAvailable;
                recorder.RecordingStopped -= OnRecordingStopped;
                recorder.Dispose();
            }
            StopSilenceMonitor();
            ClearAudioBuffer();
            throw;
        }
    }

    public void BeginVoiceCommandCapture()
    {
        lock (_sync)
        {
            if (_waveIn is null || _segments is null)
                throw new InvalidOperationException("通常の音声入力中にだけ音声コマンドを使えます。");
            if (_voiceCommandCaptureActive)
                return;

            _segments.Add(new CaptureSegmentBuffer(isVoiceCommand: true));
            _voiceCommandCaptureActive = true;
        }
    }

    public void EndVoiceCommandCapture()
    {
        lock (_sync)
        {
            if (!_voiceCommandCaptureActive || _segments is null)
                return;

            _segments.Add(new CaptureSegmentBuffer(isVoiceCommand: false));
            _voiceCommandCaptureActive = false;
        }
    }

    public async Task<float[]> StopAsync(CancellationToken cancellationToken = default)
    {
        var segments = await StopAndGetSegmentsAsync(cancellationToken);
        try
        {
            var dictationSegments = segments
                .Where(segment => !segment.IsVoiceCommand)
                .Select(segment => segment.Samples)
                .ToArray();
            var sampleCount = dictationSegments.Sum(segment => segment.Length);
            if (sampleCount < 2400)
                throw new InvalidOperationException("録音が短すぎます。もう少し話してから停止してください。");

            var combined = new float[sampleCount];
            var offset = 0;
            foreach (var segment in dictationSegments)
            {
                Array.Copy(segment, 0, combined, offset, segment.Length);
                offset += segment.Length;
            }
            return combined;
        }
        finally
        {
            foreach (var segment in segments)
                Array.Clear(segment.Samples);
        }
    }

    public async Task<IReadOnlyList<AudioRecordingSegment>> StopAndGetSegmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var recorder = _waveIn ?? throw new InvalidOperationException("録音は開始していません。");
        var stopped = _stopped!;
        recorder.StopRecording();
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            _waveIn = null;
            StopSilenceMonitor();
            ClearAudioBuffer();
            PublishAudioLevel(null);
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
        _waveIn = null;
        StopSilenceMonitor();
        PublishAudioLevel(null);

        if (_recordingError is not null)
        {
            ClearAudioBuffer();
            throw new InvalidOperationException("マイク音声を読み取れませんでした。", _recordingError);
        }

        List<(bool IsVoiceCommand, byte[] Bytes)> bytes;
        lock (_sync)
        {
            bytes = _segments?.Select(segment => (segment.IsVoiceCommand, segment.Audio.ToArray())).ToList()
                    ?? new List<(bool, byte[])>();
            ClearAudioBufferUnsafe();
        }

        var result = new List<AudioRecordingSegment>(bytes.Count);
        foreach (var (isVoiceCommand, audioBytes) in bytes)
        {
            try
            {
                var sampleCount = audioBytes.Length / sizeof(short);
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    var pcm = BinaryPrimitives.ReadInt16LittleEndian(
                        audioBytes.AsSpan(i * sizeof(short), sizeof(short)));
                    samples[i] = pcm / 32768f;
                }
                result.Add(new AudioRecordingSegment(isVoiceCommand, samples));
            }
            finally
            {
                Array.Clear(audioBytes);
            }
        }

        return result;
    }

    public async Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        var recorder = _waveIn;
        if (recorder is null)
            return;

        var stopped = _stopped!;
        try
        {
            recorder.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            _waveIn = null;
            StopSilenceMonitor();
            ClearAudioBuffer();
            PublishAudioLevel(null);
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
        _waveIn = null;
        StopSilenceMonitor();
        ClearAudioBuffer();
        PublishAudioLevel(null);
    }

    public bool TryGetPreviewSegmentsSnapshot(
        out IReadOnlyList<AudioRecordingSegment> segments,
        out int activeSegmentIndex)
    {
        List<(bool IsVoiceCommand, byte[] Bytes)> snapshots;
        lock (_sync)
        {
            if (_waveIn is null || _segments is not { Count: > 0 } currentSegments)
            {
                segments = Array.Empty<AudioRecordingSegment>();
                activeSegmentIndex = -1;
                return false;
            }

            activeSegmentIndex = currentSegments.Count - 1;
            snapshots = currentSegments
                .Select(segment => (segment.IsVoiceCommand, segment.Audio.ToArray()))
                .ToList();
        }

        var result = new List<AudioRecordingSegment>(snapshots.Count);
        try
        {
            foreach (var (isVoiceCommand, bytes) in snapshots)
            {
                try
                {
                    var sampleCount = bytes.Length / sizeof(short);
                    var samples = new float[sampleCount];
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var pcm = BinaryPrimitives.ReadInt16LittleEndian(
                            bytes.AsSpan(i * sizeof(short), sizeof(short)));
                        samples[i] = pcm / 32768f;
                    }
                    result.Add(new AudioRecordingSegment(isVoiceCommand, samples));
                }
                finally
                {
                    Array.Clear(bytes);
                }
            }

            segments = result;
            return true;
        }
        catch
        {
            foreach (var segment in result)
                Array.Clear(segment.Samples);
            activeSegmentIndex = -1;
            segments = Array.Empty<AudioRecordingSegment>();
            throw;
        }
    }

    public void Dispose()
    {
        StopSilenceMonitor();
        var recorder = _waveIn;
        _waveIn = null;
        if (recorder is not null)
        {
            try { recorder.StopRecording(); } catch { }
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
        }
        PublishAudioLevel(null);
        ClearAudioBuffer();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_sync)
            {
                if (_segments is { Count: > 0 } segments)
                    segments[^1].Audio.Write(e.Buffer, 0, e.BytesRecorded);
            }
            try
            {
                ProcessSilenceMonitorAudio(e.Buffer, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                StopSilenceMonitor();
                try { SilenceDetectionFailed?.Invoke(ex); }
                catch { }
            }
            PublishAudioLevel(AudioInputLevel.GetNormalizedLevel(e.Buffer.AsSpan(0, e.BytesRecorded)));
        }
        catch (Exception ex)
        {
            _recordingError = ex;
        }
    }

    private void PublishAudioLevel(float? level)
    {
        try { AudioLevelChanged?.Invoke(level); }
        catch { }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _recordingError = e.Exception;
        _stopped?.TrySetResult();
    }

    private void StartSilenceMonitor()
    {
        var vad = new SileroVoiceActivityDetector();
        lock (_silenceMonitorSync)
        {
            _silenceVad = vad;
            _silenceVadFrameBuffer = new byte[SileroVoiceActivityDetector.WindowBytes];
            _silenceVadFrameBufferedBytes = 0;
        }

        Interlocked.Exchange(ref _lastSpeechTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _silenceTimeoutRaised, 0);
        _silenceTimer = new System.Threading.Timer(
            CheckSilenceTimeout,
            null,
            SilenceMonitorInterval,
            SilenceMonitorInterval);
    }

    private void ProcessSilenceMonitorAudio(byte[] buffer, int bytesRecorded)
    {
        lock (_silenceMonitorSync)
        {
            if (_silenceVad is null || _silenceVadFrameBuffer is null)
                return;

            var sourceOffset = 0;
            while (sourceOffset < bytesRecorded)
            {
                var bytesToCopy = Math.Min(
                    _silenceVadFrameBuffer.Length - _silenceVadFrameBufferedBytes,
                    bytesRecorded - sourceOffset);
                Buffer.BlockCopy(
                    buffer,
                    sourceOffset,
                    _silenceVadFrameBuffer,
                    _silenceVadFrameBufferedBytes,
                    bytesToCopy);
                sourceOffset += bytesToCopy;
                _silenceVadFrameBufferedBytes += bytesToCopy;

                if (_silenceVadFrameBufferedBytes < _silenceVadFrameBuffer.Length)
                    continue;

                if (_silenceVad.AcceptPcm16Frame(_silenceVadFrameBuffer))
                    RegisterSpeechActivity();

                Array.Clear(_silenceVadFrameBuffer);
                _silenceVadFrameBufferedBytes = 0;
            }
        }
    }

    private void CheckSilenceTimeout(object? state)
    {
        if (_waveIn is null || _silenceTimer is null)
            return;

        var lastSpeechTimestamp = Interlocked.Read(ref _lastSpeechTimestamp);
        if (Stopwatch.GetElapsedTime(lastSpeechTimestamp) < SilenceAutoStopDelay)
            return;

        RaiseSilenceTimeoutOnce();
    }

    private void RegisterSpeechActivity()
    {
        var now = Stopwatch.GetTimestamp();
        var lastSpeechTimestamp = Interlocked.Read(ref _lastSpeechTimestamp);
        if (Stopwatch.GetElapsedTime(lastSpeechTimestamp, now) >= SilenceAutoStopDelay)
        {
            RaiseSilenceTimeoutOnce();
            return;
        }

        Interlocked.Exchange(ref _lastSpeechTimestamp, now);
    }

    private void RaiseSilenceTimeoutOnce()
    {
        if (Interlocked.CompareExchange(ref _silenceTimeoutRaised, 1, 0) != 0)
            return;

        try { SilenceTimeoutReached?.Invoke(); }
        catch { }
    }

    private void StopSilenceMonitor()
    {
        Interlocked.Exchange(ref _silenceTimeoutRaised, 1);
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        lock (_silenceMonitorSync)
        {
            _silenceVad?.Dispose();
            _silenceVad = null;
            if (_silenceVadFrameBuffer is not null)
                Array.Clear(_silenceVadFrameBuffer);
            _silenceVadFrameBuffer = null;
            _silenceVadFrameBufferedBytes = 0;
        }
    }

    private void ClearAudioBuffer()
    {
        lock (_sync)
            ClearAudioBufferUnsafe();
    }

    private void ClearAudioBufferUnsafe()
    {
        if (_segments is not null)
        {
            foreach (var segment in _segments)
            {
                if (segment.Audio.TryGetBuffer(out var data) && data.Array is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                        data.Array.AsSpan(data.Offset, data.Count));
                segment.Audio.Dispose();
            }
            _segments = null;
        }
        _voiceCommandCaptureActive = false;
    }

    private sealed class CaptureSegmentBuffer(bool isVoiceCommand)
    {
        public bool IsVoiceCommand { get; } = isVoiceCommand;
        public MemoryStream Audio { get; } = new();
    }
}

internal sealed record AudioRecordingSegment(bool IsVoiceCommand, float[] Samples);
