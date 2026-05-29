using System.Diagnostics;
using System.IO;
using System.Net.Http;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// Whisper.net(whisper.cpp) 기반 STT. CUDA 런타임이 있으면 GPU를, 없으면 CPU를 자동 사용한다.
/// 모델(ggml)은 최초 1회 로컬에 내려받아 캐시한다.
/// </summary>
public sealed class WhisperSpeechRecognizer : ISpeechRecognizer, IDisposable
{
    // 한국어 정확도 우선이면 LargeV3. 더 가볍게 가려면 Medium 등으로 바꾸면 된다.
    private const GgmlType Model = GgmlType.LargeV3;
    private const int WhisperSampleRate = 16000;

    // whisper.cpp 공식 ggml 모델 저장소(Hugging Face).
    private const string ModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;

    private static string ModelPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WpfMusicEditor", "models");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"ggml-{Model.ToString().ToLowerInvariant()}.bin");
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples, int sampleRate, int channels,
        string language = "ko",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0)
            return Array.Empty<TranscriptSegment>();

        var factory = await EnsureFactoryAsync(progress, cancellationToken);

        progress?.Report("전사 중...");
        var mono16k = Resample16kMono(samples, sampleRate, channels);

        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .Build();

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in processor.ProcessAsync(mono16k, cancellationToken))
            segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text.Trim()));

        return segments;
    }

    private async Task<WhisperFactory> EnsureFactoryAsync(
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_factory is not null)
            return _factory;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory is not null)
                return _factory;

            var path = ModelPath;
            if (!File.Exists(path))
                await DownloadModelAsync(path, progress, cancellationToken);

            progress?.Report("음성 인식 모델 로딩 중...");
            _factory = WhisperFactory.FromPath(path);
            return _factory;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task DownloadModelAsync(
        string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var url = ModelBaseUrl + $"ggml-{ModelHubName(Model)}.bin";
        progress?.Report($"음성 인식 모델 준비 중... ({Model}, 최초 1회)");

        using var response = await Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;

        // 중단 시 손상된 파일이 남지 않도록 임시 파일에 받은 뒤 교체한다.
        var tempPath = path + ".download";
        try
        {
            await using var modelStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var fileStream = File.Create(tempPath))
            {
                var buffer = new byte[1 << 20]; // 1MB
                long downloaded = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = TimeSpan.FromSeconds(-1);
                int read;
                while ((read = await modelStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    // UI가 너무 자주 갱신되지 않도록 0.4초마다 한 번만 보고한다.
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                    {
                        lastReport = stopwatch.Elapsed;
                        progress?.Report(FormatProgress(downloaded, total, stopwatch.Elapsed));
                    }
                }

                progress?.Report(FormatProgress(downloaded, total, stopwatch.Elapsed));
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string FormatProgress(long downloaded, long total, TimeSpan elapsed)
    {
        const double mib = 1024 * 1024;
        var doneMb = downloaded / mib;
        var speed = elapsed.TotalSeconds > 0 ? doneMb / elapsed.TotalSeconds : 0; // MB/s

        if (total <= 0)
            return $"모델 다운로드 중... {doneMb:N0}MB · {speed:0.#}MB/s";

        var totalMb = total / mib;
        var percent = downloaded * 100.0 / total;
        var remainingMb = totalMb - doneMb;
        var eta = speed > 0 ? TimeSpan.FromSeconds(remainingMb / speed) : TimeSpan.Zero;

        return $"모델 다운로드 중... {doneMb:N0}/{totalMb:N0}MB ({percent:0.#}%) · "
             + $"{speed:0.#}MB/s · 약 {FormatEta(eta)} 남음";
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}시간 {eta.Minutes}분";
        if (eta.TotalMinutes >= 1) return $"{eta.Minutes}분 {eta.Seconds}초";
        return $"{eta.Seconds}초";
    }

    /// <summary>GgmlType을 Hugging Face 파일명 규칙(예: LargeV3 → large-v3)으로 변환한다.</summary>
    private static string ModelHubName(GgmlType type) => type switch
    {
        GgmlType.Tiny => "tiny",
        GgmlType.Base => "base",
        GgmlType.Small => "small",
        GgmlType.Medium => "medium",
        GgmlType.LargeV1 => "large-v1",
        GgmlType.LargeV2 => "large-v2",
        GgmlType.LargeV3 => "large-v3",
        _ => type.ToString().ToLowerInvariant()
    };

    /// <summary>인터리브 PCM을 모노로 다운믹스한 뒤 16kHz로 리샘플링한다(Whisper 입력 규격).</summary>
    private static float[] Resample16kMono(float[] samples, int sampleRate, int channels)
    {
        var frames = samples.Length / channels;
        var mono = new float[frames];
        if (channels == 1)
        {
            Array.Copy(samples, mono, frames);
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var baseIdx = f * channels;
                for (var c = 0; c < channels; c++)
                    sum += samples[baseIdx + c];
                mono[f] = sum / channels;
            }
        }

        if (sampleRate == WhisperSampleRate)
            return mono;

        var resampler = new WdlResamplingSampleProvider(
            new MemorySampleProvider(mono, sampleRate, 1), WhisperSampleRate);

        var output = new List<float>((int)((long)frames * WhisperSampleRate / sampleRate) + 1);
        var buffer = new float[WhisperSampleRate];
        int n;
        while ((n = resampler.Read(buffer, 0, buffer.Length)) > 0)
            output.AddRange(buffer.Take(n));

        return output.ToArray();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _initLock.Dispose();
    }
}
