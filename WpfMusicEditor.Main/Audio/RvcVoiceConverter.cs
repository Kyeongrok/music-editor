using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave.SampleProviders;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// RVC(Retrieval-based Voice Conversion) generator(.onnx)로 음색을 변환한다.
/// 파이프라인: 16kHz 모노 → ContentVec 콘텐츠 특징 → f0(YIN) → generator → 원본 규격으로 리샘플.
/// 발음·내용·타이밍은 유지하고 음색만 타깃 목소리로 바꾼다.
///
/// 공유 모델(ContentVec)은 LocalAppData/WpfMusicEditor/models 에 둔다(없으면 자동 다운로드).
/// 학습된 generator(.onnx)는 사용자가 RVC로 만들어 modelPath로 넘긴다(docs/voice-conversion.md 참고).
///
/// 참고: ONNX 입출력 이름/shape/dtype은 모델 버전마다 다르므로 세션 메타데이터에서 동적으로 매핑한다.
/// f0는 onnx(RMVPE) 대신 순수 C# YIN 추정기를 써서 외부 의존성과 전처리 스케일 불일치를 피한다.
/// </summary>
public sealed class RvcVoiceConverter : IVoiceConverter, IDisposable
{
    private const int Sr16k = 16000;

    // ContentVec는 16k에서 hop 320(=50fps). f0는 hop 160(=100fps)으로 뽑으므로
    // 특징을 ×2로 늘려 f0 프레임율(100fps)에 맞춘다.
    private const int FeatureUpsample = 2;

    // f0 추정/양자화 공통 상수.
    private const double F0Min = 50.0;
    private const double F0Max = 1100.0;
    private const int F0Hop = 160;        // 16kHz에서 100fps
    private const int F0Window = 768;     // YIN 적분 창 길이
    private static readonly double F0MelMin = 1127.0 * Math.Log(1.0 + F0Min / 700.0);
    private static readonly double F0MelMax = 1127.0 * Math.Log(1.0 + F0Max / 700.0);

    // generator 출력 샘플레이트. RVC 모델은 32k/40k/48k 중 하나로 학습된다.
    // ONNX 메타데이터에 sr이 있으면 그 값을, 없으면 이 기본값을 쓴다(모델에 맞게 docs 참고).
    private const int DefaultModelSampleRate = 40000;

    // 공유 모델 파일명. 없으면 최초 1회 아래 URL에서 models 폴더로 자동 다운로드한다.
    private const string ContentVecFile = "contentvec.onnx";

    // ContentVec(v2 768차원) ONNX 배포본. RVC 생태계 공용 모델.
    private const string ContentVecUrl =
        "https://huggingface.co/DogManTC/test-rvc-onnx/resolve/main/vec-768-layer-12.onnx";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _contentVec;

    // generator는 modelPath별로 캐시한다.
    private readonly Dictionary<string, InferenceSession> _generators = new(StringComparer.OrdinalIgnoreCase);

    // generator별 고정 윈도우(프레임). -1=동적(전체 한 번에), >0=고정 길이라 그 크기로 청크.
    private readonly Dictionary<string, int> _genWindow = new(StringComparer.OrdinalIgnoreCase);

    private static string ModelsDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WpfMusicEditor", "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<float[]> ConvertAsync(
        float[] samples, int sampleRate, int channels,
        string modelPath, int semitoneShift = 0,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0)
            return Array.Empty<float>();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("음색 변환 모델(.onnx)을 찾을 수 없습니다.", modelPath);

        progress?.Report("음색 변환 준비 중...");
        percentProgress?.Report(0);

        await EnsureSharedSessionsAsync(progress, cancellationToken);
        var generator = GetGenerator(modelPath);

        // 1. 16kHz 모노로 리샘플.
        var mono16k = Resample(samples, sampleRate, channels, Sr16k, 1);

        // 무거운 추론은 호출 측에서 Task.Run으로 감싼다. 여기서는 동기 흐름으로 단계별 진행.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("콘텐츠 특징 추출 중...");
        var (feats, featFrames, featDim) = ExtractContentFeatures(mono16k);
        percentProgress?.Report(30);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("음높이(f0) 분석 중...");
        var f0 = EstimateF0(mono16k);
        if (semitoneShift != 0)
        {
            var factor = Math.Pow(2.0, semitoneShift / 12.0);
            for (var i = 0; i < f0.Length; i++)
                f0[i] = (float)(f0[i] * factor);
        }
        percentProgress?.Report(55);

        // 2. 특징 ×2 업샘플 후 f0와 길이 정렬.
        var upFrames = featFrames * FeatureUpsample;
        var pLen = Math.Min(upFrames, f0.Length);
        if (pLen <= 0)
            return Array.Empty<float>();

        var featsUp = UpsampleFeatures(feats, featFrames, featDim, pLen);
        var (pitch, pitchf) = BuildPitch(f0, pLen);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("음색 변환(generator) 실행 중...");
        var (wave, modelSr) = RunGeneratorAdaptive(modelPath, generator, featsUp, pLen, featDim, pitch, pitchf, progress);
        percentProgress?.Report(85);

        // 3. 원본 샘플레이트·채널로 되돌린다.
        progress?.Report("출력 정리 중...");
        var outInterleaved = Resample(wave, modelSr, 1, sampleRate, channels);
        percentProgress?.Report(100);
        return outInterleaved;
    }

    // ── ContentVec ────────────────────────────────────────────────

    /// <summary>16k 모노 → 콘텐츠 특징 [frames, dim]을 평탄 배열로 돌려준다.</summary>
    private (float[] feats, int frames, int dim) ExtractContentFeatures(float[] mono16k)
    {
        var session = _contentVec!;
        var inputMeta = session.InputMetadata.First();
        var rank = inputMeta.Value.Dimensions.Length;

        // 모델이 선언한 랭크에 맞춰 오디오 텐서를 만든다([1,1,N] 또는 [1,N]).
        var audio = rank >= 3
            ? new DenseTensor<float>(mono16k, new[] { 1, 1, mono16k.Length })
            : new DenseTensor<float>(mono16k, new[] { 1, mono16k.Length });

        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(inputMeta.Key, audio)
        });

        var outTensor = results.First().AsTensor<float>();
        var dims = outTensor.Dimensions;            // 보통 [1, frames, dim]
        var frames = dims[^2];
        var dim = dims[^1];
        return (outTensor.ToArray(), frames, dim);
    }

    /// <summary>특징을 시간축으로 <see cref="FeatureUpsample"/>배 반복(repeat)한 뒤 pLen 프레임으로 자른다.</summary>
    private static float[] UpsampleFeatures(float[] feats, int frames, int dim, int pLen)
    {
        var outBuf = new float[pLen * dim];
        for (var t = 0; t < pLen; t++)
        {
            var src = Math.Min(t / FeatureUpsample, frames - 1);
            Array.Copy(feats, src * dim, outBuf, t * dim, dim);
        }
        return outBuf;
    }

    // ── f0 (YIN, 순수 C#) ─────────────────────────────────────────

    /// <summary>16k 모노 → 프레임별 f0(Hz) 배열(100fps). 무성 구간은 0. YIN 알고리즘.</summary>
    private static float[] EstimateF0(float[] mono16k)
    {
        const double yinThreshold = 0.15;  // 유성 판정 임계값(작을수록 엄격)
        var minTau = (int)(Sr16k / F0Max);  // 최고 음(짧은 주기)
        var maxTau = (int)(Sr16k / F0Min);  // 최저 음(긴 주기)

        var frames = Math.Max(0, (mono16k.Length - F0Window - maxTau) / F0Hop + 1);
        var f0 = new float[frames];
        var diff = new double[maxTau + 1];
        var cmnd = new double[maxTau + 1];

        for (var t = 0; t < frames; t++)
        {
            var start = t * F0Hop;

            // 1. 차이 함수 d(τ).
            for (var tau = 1; tau <= maxTau; tau++)
            {
                double sum = 0;
                for (var j = 0; j < F0Window; j++)
                {
                    var d = mono16k[start + j] - mono16k[start + j + tau];
                    sum += d * d;
                }
                diff[tau] = sum;
            }

            // 2. 누적평균 정규화 차이 함수 d'(τ).
            cmnd[0] = 1.0;
            double running = 0;
            for (var tau = 1; tau <= maxTau; tau++)
            {
                running += diff[tau];
                cmnd[tau] = running > 0 ? diff[tau] * tau / running : 1.0;
            }

            // 3. 임계값 아래 첫 국소 최소 τ를 찾고, 없으면 전체 최소.
            var bestTau = -1;
            for (var tau = minTau; tau < maxTau; tau++)
            {
                if (cmnd[tau] < yinThreshold && cmnd[tau] <= cmnd[tau + 1])
                {
                    bestTau = tau;
                    break;
                }
            }
            if (bestTau < 0)
            {
                var minVal = double.MaxValue;
                for (var tau = minTau; tau <= maxTau; tau++)
                    if (cmnd[tau] < minVal) { minVal = cmnd[tau]; bestTau = tau; }
                // 유성으로 보기엔 신뢰도가 낮으면 무성 처리.
                if (bestTau < 0 || cmnd[bestTau] > 0.5) { f0[t] = 0f; continue; }
            }

            // 4. 포물선 보간으로 τ를 세밀화.
            var tauEst = (double)bestTau;
            if (bestTau > minTau && bestTau < maxTau)
            {
                var s0 = cmnd[bestTau - 1];
                var s1 = cmnd[bestTau];
                var s2 = cmnd[bestTau + 1];
                var denom = 2.0 * (2.0 * s1 - s0 - s2);
                if (Math.Abs(denom) > 1e-12)
                    tauEst = bestTau + (s2 - s0) / denom;
            }

            var hz = Sr16k / tauEst;
            f0[t] = hz is >= F0Min and <= F0Max ? (float)hz : 0f;
        }
        return f0;
    }

    /// <summary>f0(Hz) → (coarse 정수 1~255, 연속 f0) 두 벡터를 pLen 길이로 만든다.</summary>
    private static (long[] pitch, float[] pitchf) BuildPitch(float[] f0, int pLen)
    {
        var pitch = new long[pLen];
        var pitchf = new float[pLen];
        for (var t = 0; t < pLen; t++)
        {
            var hz = t < f0.Length ? f0[t] : 0f;
            pitchf[t] = hz;
            if (hz <= 0f) { pitch[t] = 0; continue; }

            var mel = 1127.0 * Math.Log(1.0 + hz / 700.0);
            var coarse = (mel - F0MelMin) * 254.0 / (F0MelMax - F0MelMin) + 1.0;
            if (coarse < 1.0) coarse = 1.0;
            if (coarse > 255.0) coarse = 255.0;
            pitch[t] = (long)Math.Round(coarse);
        }
        return (pitch, pitchf);
    }

    // ── generator ────────────────────────────────────────────────

    /// <summary>
    /// generator 실행. 동적 export 모델은 전체를 한 번에, 고정 길이 export 모델은
    /// 고정 윈도우로 청크 추론한다(상대위치 어텐션이 고정 길이로 굳은 모델 대응).
    /// </summary>
    private (float[] wave, int sampleRate) RunGeneratorAdaptive(
        string modelPath, InferenceSession session, float[] featsUp, int pLen, int dim,
        long[] pitch, float[] pitchf, IProgress<string>? progress)
    {
        bool known;
        int window;
        lock (_genWindow) known = _genWindow.TryGetValue(modelPath, out window);

        if (!known)
        {
            // 첫 시도: 전체 길이로 실행. 동적 모델이면 그대로 성공한다.
            try
            {
                var r = RunGeneratorWindow(session, featsUp, pLen, dim, pitch, pitchf);
                lock (_genWindow) _genWindow[modelPath] = -1; // 동적 확정
                return r;
            }
            catch (OnnxRuntimeException ex) when (TryParseFixedWindow(ex.Message, pLen, out window))
            {
                lock (_genWindow) _genWindow[modelPath] = window;
                progress?.Report($"고정 길이 모델 감지(윈도우 {window}프레임) · 청크 단위로 변환합니다");
            }
        }

        if (window <= 0)
            return RunGeneratorWindow(session, featsUp, pLen, dim, pitch, pitchf);
        return RunGeneratorChunked(session, featsUp, pLen, dim, pitch, pitchf, window, progress);
    }

    /// <summary>
    /// 고정 길이 export 모델의 Reshape 오류 메시지에서 필요한 윈도우 길이를 역산한다.
    /// 상대위치 어텐션이 길이 L0으로 export되면 패딩량 (L0−1)이 상수로 굳는다.
    /// 그래서 입력 numel = 2·L² + (L0−1) 형태가 되고, 모델이 요구하는 2·L²+L−1 과 일치하려면 L=L0.
    /// 즉 윈도우 L0 = (numel − 2·L²) + 1.
    /// </summary>
    private static bool TryParseFixedWindow(string message, int triedLen, out int window)
    {
        window = 0;
        var m = Regex.Match(message, @"Input shape:\{([0-9,]+)\}");
        if (!m.Success)
            return false;
        var parts = m.Groups[1].Value.Split(',');
        if (!long.TryParse(parts[^1], out var numel))
            return false;

        var c = numel - 2L * triedLen * triedLen; // = L0 − 1
        var w = c + 1;
        if (w < 16 || w > 8192)
            return false;
        window = (int)w;
        return true;
    }

    /// <summary>고정 윈도우 모델: pLen을 window 단위로 잘라 추론하고 출력을 이어 붙인다.</summary>
    private (float[] wave, int sampleRate) RunGeneratorChunked(
        InferenceSession session, float[] featsUp, int pLen, int dim,
        long[] pitch, float[] pitchf, int window, IProgress<string>? progress)
    {
        var chunks = new List<float[]>();
        var sr = DefaultModelSampleRate;
        var hop = 0; // 프레임당 출력 샘플 수(첫 청크에서 학습)
        var totalChunks = (pLen + window - 1) / window;

        for (int pos = 0, idx = 0; pos < pLen; pos += window, idx++)
        {
            var real = Math.Min(window, pLen - pos);

            // window 크기로 채우되, 모자란 꼬리는 마지막 실프레임을 반복해 패딩한다.
            var fW = new float[window * dim];
            var pW = new long[window];
            var pfW = new float[window];
            for (var t = 0; t < window; t++)
            {
                var src = pos + Math.Min(t, real - 1);
                Array.Copy(featsUp, src * dim, fW, t * dim, dim);
                pW[t] = pitch[src];
                pfW[t] = pitchf[src];
            }

            progress?.Report($"음색 변환(generator) {idx + 1}/{totalChunks} 청크...");
            var (w, s) = RunGeneratorWindow(session, fW, window, dim, pW, pfW);
            sr = s;
            if (hop == 0) hop = Math.Max(1, w.Length / window);

            // 패딩으로 만든 부분은 버리고 실제 프레임에 해당하는 만큼만 취한다.
            var keep = Math.Min(real * hop, w.Length);
            var seg = new float[keep];
            Array.Copy(w, 0, seg, 0, keep);
            chunks.Add(seg);
        }

        var total = chunks.Sum(c => c.Length);
        var wave = new float[total];
        var o = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, wave, o, c.Length); o += c.Length; }
        return (wave, sr);
    }

    /// <summary>윈도우 한 번 분량의 generator 추론. 입력 이름은 세션 메타데이터에서 역할별로 매핑한다.</summary>
    private (float[] wave, int sampleRate) RunGeneratorWindow(
        InferenceSession session, float[] featsUp, int pLen, int dim, long[] pitch, float[] pitchf)
    {
        var feeds = new List<NamedOnnxValue>();
        var fp16 = false;

        foreach (var input in session.InputMetadata)
        {
            var name = input.Key.ToLowerInvariant();
            var role = ClassifyInput(name);
            switch (role)
            {
                case InputRole.Feats:
                    fp16 = input.Value.ElementType == typeof(Float16);
                    feeds.Add(MakeFeatsValue(input.Key, featsUp, pLen, dim, fp16));
                    break;
                case InputRole.Length:
                    feeds.Add(NamedOnnxValue.CreateFromTensor(
                        input.Key, new DenseTensor<long>(new[] { (long)pLen }, new[] { 1 })));
                    break;
                case InputRole.Pitch:
                    feeds.Add(NamedOnnxValue.CreateFromTensor(
                        input.Key, new DenseTensor<long>(pitch, new[] { 1, pLen })));
                    break;
                case InputRole.PitchF:
                    feeds.Add(MakePitchfValue(input.Key, pitchf, pLen, input.Value.ElementType == typeof(Float16)));
                    break;
                case InputRole.SpeakerId:
                    feeds.Add(NamedOnnxValue.CreateFromTensor(
                        input.Key, new DenseTensor<long>(new[] { 0L }, new[] { 1 })));
                    break;
                case InputRole.Rnd:
                    // generator의 노이즈 입력. shape [1, latent(=192), pLen]. 채널 수는 모델에서 읽는다.
                    var dimsR = input.Value.Dimensions;
                    var latent = dimsR.Length >= 2 && dimsR[1] > 0 ? dimsR[1] : 192;
                    feeds.Add(NamedOnnxValue.CreateFromTensor(
                        input.Key, new DenseTensor<float>(new float[latent * pLen], new[] { 1, latent, pLen })));
                    break;
            }
        }

        using var results = session.Run(feeds);
        var outValue = results.First();
        var wave = ToFloatArray(outValue);

        var sr = DefaultModelSampleRate;
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("sr", out var srStr)
            && int.TryParse(srStr, out var parsed))
            sr = parsed;
        else if (session.ModelMetadata.CustomMetadataMap.TryGetValue("sample_rate", out var srStr2)
            && int.TryParse(srStr2, out var parsed2))
            sr = parsed2;

        return (wave, sr);
    }

    private enum InputRole { Feats, Length, Pitch, PitchF, SpeakerId, Rnd, Unknown }

    private static InputRole ClassifyInput(string name) => name switch
    {
        "feats" or "phone" or "hubert" or "c" or "audio" => InputRole.Feats,
        "p_len" or "feats_lengths" or "phone_lengths" or "length" or "lengths" => InputRole.Length,
        "pitch" => InputRole.Pitch,
        "pitchf" or "nsff0" => InputRole.PitchF,
        "sid" or "ds" or "g" or "spk" => InputRole.SpeakerId,
        "rnd" or "noise" => InputRole.Rnd,
        _ => InputRole.Unknown
    };

    private static NamedOnnxValue MakeFeatsValue(string name, float[] featsUp, int pLen, int dim, bool fp16)
    {
        if (!fp16)
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(featsUp, new[] { 1, pLen, dim }));

        var half = new Float16[featsUp.Length];
        for (var i = 0; i < featsUp.Length; i++)
            half[i] = (Float16)featsUp[i];
        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(half, new[] { 1, pLen, dim }));
    }

    private static NamedOnnxValue MakePitchfValue(string name, float[] pitchf, int pLen, bool fp16)
    {
        if (!fp16)
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(pitchf, new[] { 1, pLen }));

        var half = new Float16[pitchf.Length];
        for (var i = 0; i < pitchf.Length; i++)
            half[i] = (Float16)pitchf[i];
        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(half, new[] { 1, pLen }));
    }

    private static float[] ToFloatArray(DisposableNamedOnnxValue value)
    {
        if (value.ElementType == TensorElementType.Float16)
        {
            var t = value.AsTensor<Float16>().ToArray();
            var f = new float[t.Length];
            for (var i = 0; i < t.Length; i++)
                f[i] = (float)t[i];
            return f;
        }
        return value.AsTensor<float>().ToArray();
    }

    // ── 세션 준비 ─────────────────────────────────────────────────

    private async Task EnsureSharedSessionsAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_contentVec is not null)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_contentVec is null)
            {
                var path = await EnsureSharedModelAsync(ContentVecFile, ContentVecUrl, "발음 특징", progress, cancellationToken);
                _contentVec = CreateSession(path);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>공유 모델이 없으면 최초 1회 다운로드한 뒤 경로를 돌려준다(Whisper 모델 캐시와 동일 방식).</summary>
    private static async Task<string> EnsureSharedModelAsync(
        string fileName, string url, string label, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ModelsDir, fileName);
        if (File.Exists(path))
            return path;

        progress?.Report($"{label} 모델 준비 중... (최초 1회)");

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                    // 0.4초마다 한 번만 진행 상황을 보고한다.
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                    {
                        lastReport = stopwatch.Elapsed;
                        progress?.Report(FormatDownload(label, downloaded, total, stopwatch.Elapsed));
                    }
                }
            }

            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException(
                $"공유 음성 모델 '{fileName}' 다운로드에 실패했습니다. " +
                $"수동으로 받아 다음 폴더에 넣어도 됩니다:\n{ModelsDir}\n" +
                "받는 방법은 docs/voice-conversion.md를 참고하세요.", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string FormatDownload(string label, long downloaded, long total, TimeSpan elapsed)
    {
        const double mib = 1024 * 1024;
        var doneMb = downloaded / mib;
        var speed = elapsed.TotalSeconds > 0 ? doneMb / elapsed.TotalSeconds : 0; // MB/s
        if (total <= 0)
            return $"{label} 모델 다운로드 중... {doneMb:N0}MB · {speed:0.#}MB/s";

        var totalMb = total / mib;
        var percent = downloaded * 100.0 / total;
        return $"{label} 모델 다운로드 중... {doneMb:N0}/{totalMb:N0}MB ({percent:0.#}%) · {speed:0.#}MB/s";
    }

    private InferenceSession GetGenerator(string modelPath)
    {
        lock (_generators)
        {
            if (_generators.TryGetValue(modelPath, out var existing))
                return existing;
            var session = CreateSession(modelPath);
            _generators[modelPath] = session;
            return session;
        }
    }

    /// <summary>DirectML(GPU) 우선, 실패하면 CPU로 ONNX 세션을 만든다.</summary>
    private static InferenceSession CreateSession(string modelPath)
    {
        try
        {
            var options = new SessionOptions();
            options.AppendExecutionProvider_DML();
            return new InferenceSession(modelPath, options);
        }
        catch
        {
            // DirectML을 못 쓰면(드라이버/GPU 부재) 기본(CPU) 세션으로 폴백.
            return new InferenceSession(modelPath);
        }
    }

    // ── 리샘플 ────────────────────────────────────────────────────

    /// <summary>인터리브 PCM을 (모노 다운믹스 후) 목표 샘플레이트/채널로 리샘플한다.</summary>
    private static float[] Resample(float[] samples, int srcRate, int srcChannels, int dstRate, int dstChannels)
    {
        var frames = samples.Length / srcChannels;
        var mono = new float[frames];
        if (srcChannels == 1)
        {
            Array.Copy(samples, mono, frames);
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var baseIdx = f * srcChannels;
                for (var c = 0; c < srcChannels; c++)
                    sum += samples[baseIdx + c];
                mono[f] = sum / srcChannels;
            }
        }

        float[] monoOut;
        if (srcRate == dstRate)
        {
            monoOut = mono;
        }
        else
        {
            var resampler = new WdlResamplingSampleProvider(
                new MemorySampleProvider(mono, srcRate, 1), dstRate);
            var outList = new List<float>((int)((long)frames * dstRate / srcRate) + 1);
            var buffer = new float[dstRate];
            int n;
            while ((n = resampler.Read(buffer, 0, buffer.Length)) > 0)
                outList.AddRange(buffer.Take(n));
            monoOut = outList.ToArray();
        }

        if (dstChannels == 1)
            return monoOut;

        // 모노 → 다채널: 같은 값으로 복제.
        var interleaved = new float[monoOut.Length * dstChannels];
        for (var f = 0; f < monoOut.Length; f++)
            for (var c = 0; c < dstChannels; c++)
                interleaved[f * dstChannels + c] = monoOut[f];
        return interleaved;
    }

    public void Dispose()
    {
        _contentVec?.Dispose();
        lock (_generators)
        {
            foreach (var s in _generators.Values)
                s.Dispose();
            _generators.Clear();
        }
        _initLock.Dispose();
    }
}
