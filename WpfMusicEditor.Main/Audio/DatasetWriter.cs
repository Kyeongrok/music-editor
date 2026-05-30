using System.IO;
using NAudio.Wave;

namespace WpfMusicEditor.Main.Audio;

/// <summary>학습 데이터셋용 wav 내보내기. 인터리브 PCM을 모노 16-bit wav로 쓴다(RVC가 알아서 리샘플).</summary>
public static class DatasetWriter
{
    /// <summary>인터리브 float PCM 구간을 모노로 다운믹스해 16-bit wav로 저장한다.</summary>
    public static void WriteMonoWav(string path, float[] interleaved, int sampleRate, int channels)
    {
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        if (channels == 1)
        {
            Array.Copy(interleaved, mono, frames);
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var baseIdx = f * channels;
                for (var c = 0; c < channels; c++)
                    sum += interleaved[baseIdx + c];
                mono[f] = sum / channels;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var provider = new MemorySampleProvider(mono, sampleRate, 1);
        WaveFileWriter.CreateWaveFile16(path, provider);
    }
}
