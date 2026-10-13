using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class MicrophoneRecorder : IDisposable
{
    private readonly object _sync = new();
    private WaveInEvent? _waveIn;
    private MemoryStream? _audio;
    private TaskCompletionSource? _stopped;
    private Exception? _recordingError;

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

        _audio = new MemoryStream();
        _recordingError = null;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var recorder = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
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
            recorder.Dispose();
            ClearAudioBuffer();
            throw;
        }
    }

    public async Task<float[]> StopAsync(CancellationToken cancellationToken = default)
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
            throw;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
        _waveIn = null;

        if (_recordingError is not null)
        {
            ClearAudioBuffer();
            throw new InvalidOperationException("マイク音声を読み取れませんでした。", _recordingError);
        }

        byte[] bytes;
        lock (_sync)
            bytes = _audio?.ToArray() ?? Array.Empty<byte>();
        ClearAudioBuffer();

        var sampleCount = bytes.Length / sizeof(short);
        if (sampleCount < 2400)
        {
            Array.Clear(bytes);
            throw new InvalidOperationException("録音が短すぎます。もう少し話してから停止してください。");
        }

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var pcm = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * sizeof(short), sizeof(short)));
            samples[i] = pcm / 32768f;
        }
        Array.Clear(bytes);
        return samples;
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
        ClearAudioBuffer();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_sync)
                _audio?.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            _recordingError = ex;
        }
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
        {
            if (_audio is not null)
            {
                if (_audio.TryGetBuffer(out var segment) && segment.Array is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                        segment.Array.AsSpan(segment.Offset, segment.Count));
                _audio.Dispose();
                _audio = null;
            }
        }
    }
}
