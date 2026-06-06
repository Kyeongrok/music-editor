using NAudio.Wave.SampleProviders;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 인터리브 PCM을 목표 샘플레이트·채널 수에 맞게 변환한다. 다른 파일의 구간을
/// 현재 문서에 끼워 넣을 때, 두 파일의 포맷이 달라도 그대로 이어 붙일 수 있게 한다.
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// <paramref name="samples"/>(srcRate/srcChannels)을 dstRate/dstChannels로 변환한다.
    /// 채널은 모노↔다채널을 복제/평균으로 맞추고, 샘플레이트는 채널별로 리샘플한다.
    /// </summary>
    public static float[] Resample(float[] samples, int srcRate, int srcChannels, int dstRate, int dstChannels)
    {
        if (samples.Length == 0)
            return Array.Empty<float>();
        if (srcRate == dstRate && srcChannels == dstChannels)
            return samples;

        var srcFrames = samples.Length / srcChannels;

        // 1) 원본을 목표 채널 수의 모노 채널들로 바꾼다(아직 원본 샘플레이트).
        var channels = new float[dstChannels][];
        for (var c = 0; c < dstChannels; c++)
            channels[c] = new float[srcFrames];

        for (var f = 0; f < srcFrames; f++)
        {
            var baseIdx = f * srcChannels;
            if (dstChannels == 1)
            {
                // 다채널 → 모노: 평균으로 다운믹스.
                var sum = 0f;
                for (var c = 0; c < srcChannels; c++)
                    sum += samples[baseIdx + c];
                channels[0][f] = sum / srcChannels;
            }
            else
            {
                for (var c = 0; c < dstChannels; c++)
                {
                    // 모노 원본은 모든 채널에 복제, 그 외엔 같은 채널(없으면 마지막)을 가져온다.
                    var srcC = srcChannels == 1 ? 0 : Math.Min(c, srcChannels - 1);
                    channels[c][f] = samples[baseIdx + srcC];
                }
            }
        }

        // 2) 필요하면 채널별로 리샘플한다.
        if (srcRate != dstRate)
            for (var c = 0; c < dstChannels; c++)
                channels[c] = ResampleMono(channels[c], srcRate, dstRate);

        // 3) 다시 인터리브로 합친다.
        var outFrames = channels[0].Length;
        var result = new float[outFrames * dstChannels];
        for (var f = 0; f < outFrames; f++)
            for (var c = 0; c < dstChannels; c++)
                result[f * dstChannels + c] = channels[c][f];
        return result;
    }

    private static float[] ResampleMono(float[] mono, int srcRate, int dstRate)
    {
        if (mono.Length == 0)
            return mono;

        var resampler = new WdlResamplingSampleProvider(
            new MemorySampleProvider(mono, srcRate, 1), dstRate);
        var outList = new List<float>((int)((long)mono.Length * dstRate / srcRate) + 1);
        var buffer = new float[dstRate];
        int n;
        while ((n = resampler.Read(buffer, 0, buffer.Length)) > 0)
            outList.AddRange(buffer.Take(n));
        return outList.ToArray();
    }
}
