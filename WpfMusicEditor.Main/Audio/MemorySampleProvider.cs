using NAudio.Wave;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 메모리의 float 샘플 배열을 재생/인코딩에 쓰는 ISampleProvider. 임의 위치로 탐색 가능.
/// </summary>
public sealed class MemorySampleProvider : ISampleProvider
{
    private readonly float[] _samples;

    public MemorySampleProvider(float[] samples, int sampleRate, int channels)
    {
        _samples = samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>현재 위치(인터리브 샘플 인덱스).</summary>
    public long Position { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var remaining = _samples.Length - Position;
        if (remaining <= 0)
            return 0;

        var n = (int)Math.Min(count, remaining);
        Array.Copy(_samples, Position, buffer, offset, n);
        Position += n;
        return n;
    }
}
