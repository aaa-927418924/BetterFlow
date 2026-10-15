using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class MicrophoneRecorder : IDisposable
{
    private readonly object _sync = new();
    private WaveInEvent? _waveIn;
    private List<CaptureSegmentBuffer>? _segments;
    private TaskCompletionSource? _stopped;
    private Exception? _recordingError;
    private bool _voiceCommandCaptureActive;

    public event Action<float?>? AudioLevelChanged;

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

    public void Start(int deviceNumber = -1)
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

        var recorder = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
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
            recorder.Dispose();
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
            ClearAudioBuffer();
            PublishAudioLevel(null);
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
        _waveIn = null;
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

    public bool TryGetRecordingSnapshot(out float[] samples)
    {
        byte[] bytes;
        lock (_sync)
        {
            var dictationBytes = _segments?
                .Where(segment => !segment.IsVoiceCommand)
                .Select(segment => segment.Audio.ToArray())
                .ToArray() ?? Array.Empty<byte[]>();
            bytes = CombineAndClear(dictationBytes);

            if (_waveIn is null || bytes.Length < 16000 * sizeof(short))
            {
                Array.Clear(bytes);
                samples = Array.Empty<float>();
                return false;
            }
        }

        var sampleCount = bytes.Length / sizeof(short);
        try
        {
            samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var pcm = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * sizeof(short), sizeof(short)));
                samples[i] = pcm / 32768f;
            }
            return true;
        }
        finally
        {
            Array.Clear(bytes);
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

    private static byte[] CombineAndClear(IEnumerable<byte[]> segments)
    {
        var arrays = segments.ToArray();
        try
        {
            var size = arrays.Sum(segment => segment.Length);
            var combined = new byte[size];
            var offset = 0;
            foreach (var segment in arrays)
            {
                Buffer.BlockCopy(segment, 0, combined, offset, segment.Length);
                offset += segment.Length;
            }
            return combined;
        }
        finally
        {
            foreach (var segment in arrays)
                Array.Clear(segment);
        }
    }

    private sealed class CaptureSegmentBuffer(bool isVoiceCommand)
    {
        public bool IsVoiceCommand { get; } = isVoiceCommand;
        public MemoryStream Audio { get; } = new();
    }
}

internal sealed record AudioRecordingSegment(bool IsVoiceCommand, float[] Samples);
