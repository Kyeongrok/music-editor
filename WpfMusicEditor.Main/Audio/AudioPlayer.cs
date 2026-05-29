using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 메모리 샘플을 미리듣기용으로 재생한다. 편집(Cut/Undo)으로 샘플이 바뀌면
/// <see cref="LoadSamples"/>로 다시 로드한다.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private MemorySampleProvider? _provider;
    private int _sampleRate = 44100;
    private int _channels = 2;

    /// <summary>재생이 (끝까지 또는 명시적으로) 멈췄을 때 발생. UI 스레드로 마샬링된다.</summary>
    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan CurrentTime =>
        _provider is null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)_provider.Position / _channels / _sampleRate);

    /// <summary>재생할 샘플을 교체한다. 기존 재생은 정지된다.</summary>
    public void LoadSamples(float[] samples, int sampleRate, int channels)
    {
        DisposeDevice();
        _sampleRate = sampleRate;
        _channels = channels;
        _provider = new MemorySampleProvider(samples, sampleRate, channels);
    }

    /// <summary>지정 위치로 이동한 뒤 재생한다.</summary>
    public void Play(TimeSpan from)
    {
        if (_provider is null)
            return;

        if (_output is null)
        {
            _output = new WaveOutEvent();
            _output.PlaybackStopped += OnOutputStopped;
            // 레거시 waveOut 장치는 32비트 float을 재생하지 못하는 경우가 많아 16비트 PCM으로 변환해 넘긴다.
            // 위치 탐색은 하부 _provider.Position 으로 그대로 제어된다.
            _output.Init(new SampleToWaveProvider16(_provider));
        }

        _provider.Position = ToSampleIndex(from);
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    public void Stop() => _output?.Stop();

    private long ToSampleIndex(TimeSpan time)
    {
        var frame = (long)Math.Round(time.TotalSeconds * _sampleRate);
        var idx = frame * _channels;
        return idx < 0 ? 0 : idx;
    }

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
        => PlaybackStopped?.Invoke(this, EventArgs.Empty);

    private void DisposeDevice()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnOutputStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _provider = null;
    }

    public void Dispose() => DisposeDevice();
}
