namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 인터리브 float PCM 구간을 받아, 학습된 RVC 모델(.onnx)의 음색으로 변환한다.
/// 발음·내용·타이밍은 유지하고 음색(timbre)만 타깃 목소리로 바꾼다.
/// </summary>
public interface IVoiceConverter
{
    /// <param name="samples">인터리브된 float PCM 샘플(구간만).</param>
    /// <param name="sampleRate">원본 샘플레이트.</param>
    /// <param name="channels">원본 채널 수. 반환값도 같은 채널/샘플레이트로 맞춰 돌려준다.</param>
    /// <param name="modelPath">타깃 음색으로 학습·export된 RVC generator ONNX 경로.</param>
    /// <param name="semitoneShift">피치 시프트(반음 단위). 0이면 원래 음높이 유지.</param>
    /// <param name="progress">모델 다운로드/진행 상태 텍스트 알림.</param>
    /// <param name="percentProgress">변환 진행률(0~100) 알림. 프로그레스바용.</param>
    /// <returns>변환된 인터리브 PCM(원본 채널 수·샘플레이트 기준).</returns>
    Task<float[]> ConvertAsync(
        float[] samples, int sampleRate, int channels,
        string modelPath, int semitoneShift = 0,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken cancellationToken = default);
}
