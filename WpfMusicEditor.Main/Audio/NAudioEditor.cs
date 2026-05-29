using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// Windows Media Foundation 기반 구현. m4a(AAC) 디코딩과 m4a/mp3 인코딩, wav 쓰기를 지원한다.
/// </summary>
public sealed class NAudioEditor : IAudioEditor
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            MediaFoundationApi.Startup();
            _initialized = true;
        }
    }

    public Task<AudioDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Load(path, cancellationToken), cancellationToken);

    private static AudioDocument Load(string path, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        using var reader = new MediaFoundationReader(path);
        var sampleProvider = reader.ToSampleProvider();
        var channels = sampleProvider.WaveFormat.Channels;
        var sampleRate = sampleProvider.WaveFormat.SampleRate;

        // 길이 추정값 + 여유분으로 미리 잡고, 모자라면 두 배씩 키운다.
        var estimated = (long)(reader.TotalTime.TotalSeconds * sampleRate + sampleRate) * channels;
        var samples = new float[Math.Max(estimated, sampleRate * channels)];

        var buffer = new float[sampleRate * channels];
        var total = 0;
        int read;
        while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total + read > samples.Length)
                Array.Resize(ref samples, Math.Max(samples.Length * 2, total + read));
            Array.Copy(buffer, 0, samples, total, read);
            total += read;
        }

        if (total != samples.Length)
            Array.Resize(ref samples, total);

        return new AudioDocument(sampleRate, channels, samples);
    }

    public Task ExportAsync(AudioDocument document, string outputPath, AudioFormat format,
        int bitrateBps = 192000, CancellationToken cancellationToken = default)
        => Task.Run(() => Export(document, outputPath, format, bitrateBps, cancellationToken), cancellationToken);

    private static void Export(AudioDocument document, string outputPath, AudioFormat format,
        int bitrateBps, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();

        var source = new MemorySampleProvider(document.Samples, document.SampleRate, document.Channels);
        IWaveProvider pcm = source.ToWaveProvider16();

        // Media Foundation 인코더는 확장자로 컨테이너를 정하므로, 임시 파일도 원래 확장자를 유지해야 한다.
        var dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var tempPath = Path.Combine(dir, "~" + Path.GetFileName(outputPath));
        if (File.Exists(tempPath))
            File.Delete(tempPath);
        try
        {
            switch (format)
            {
                case AudioFormat.Wav:
                    WaveFileWriter.CreateWaveFile(tempPath, pcm);
                    break;
                case AudioFormat.Mp3:
                    MediaFoundationEncoder.EncodeToMp3(pcm, tempPath, bitrateBps);
                    break;
                case AudioFormat.M4a:
                    MediaFoundationEncoder.EncodeToAac(pcm, tempPath, bitrateBps);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "지원하지 않는 포맷입니다.");
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
