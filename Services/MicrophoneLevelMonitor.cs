using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class MicrophoneLevelMonitor : IDisposable
{
    private WaveInEvent? _waveIn;
    private int _deviceNumber = -1;

    public event Action<float?>? AudioLevelChanged;

    public bool IsMonitoring => _waveIn is not null;
    public int DeviceNumber => _deviceNumber;

    public void Start(int deviceNumber)
    {
        if (_waveIn is not null && _deviceNumber == deviceNumber)
            return;

        Stop();
        if (WaveInEvent.DeviceCount == 0 || deviceNumber < -1 || deviceNumber >= WaveInEvent.DeviceCount)
            return;

        var recorder = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        _waveIn = recorder;
        _deviceNumber = deviceNumber;

        try
        {
            recorder.StartRecording();
        }
        catch
        {
            _waveIn = null;
            _deviceNumber = -1;
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            PublishAudioLevel(0);
            throw;
        }
    }

    public void Stop()
    {
        var recorder = _waveIn;
        _waveIn = null;
        _deviceNumber = -1;
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            try { recorder.StopRecording(); } catch { }
            recorder.Dispose();
        }
        PublishAudioLevel(0);
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        try
        {
            PublishAudioLevel(AudioInputLevel.GetNormalizedLevel(e.Buffer.AsSpan(0, e.BytesRecorded)));
        }
        catch
        {
            PublishAudioLevel(0);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveInEvent recorder || !ReferenceEquals(recorder, _waveIn))
            return;

        _waveIn = null;
        _deviceNumber = -1;
        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
        PublishAudioLevel(0);
    }

    private void PublishAudioLevel(float? level)
    {
        try { AudioLevelChanged?.Invoke(level); }
        catch { }
    }
}
