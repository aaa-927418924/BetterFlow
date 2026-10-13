using System.IO;
using System.Reflection;
using NAudio.Wave;

namespace BetterFlow.Services;

internal sealed class ActivationSoundPlayer : IDisposable
{
    private const string ResourceName = "BetterFlow.Assets.activation.mp3";
    private readonly object _sync = new();
    private Stream? _resourceStream;
    private Mp3FileReader? _reader;
    private WaveOutEvent? _output;

    public void Play()
    {
        lock (_sync)
        {
            StopPlaybackLocked();
            try
            {
                _resourceStream = typeof(ActivationSoundPlayer).Assembly.GetManifestResourceStream(ResourceName);
                if (_resourceStream is null) return;

                _reader = new Mp3FileReader(_resourceStream);
                _output = new WaveOutEvent();
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_reader);
                _output.Play();
            }
            catch
            {
                StopPlaybackLocked();
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _output)) return;

            var output = _output;
            _output = null;
            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Dispose();
            }

            _reader?.Dispose();
            _reader = null;
            _resourceStream?.Dispose();
            _resourceStream = null;
        }
    }

    private void StopPlaybackLocked()
    {
        var output = _output;
        _output = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); }
            catch { }
            output.Dispose();
        }

        _reader?.Dispose();
        _reader = null;
        _resourceStream?.Dispose();
        _resourceStream = null;
    }

    public void Dispose()
    {
        lock (_sync)
            StopPlaybackLocked();
    }
}
