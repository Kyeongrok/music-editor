namespace WpfMusicEditor.Main.Audio;

/// <summary>전사 결과 한 조각. 타임스탬프는 넘긴 오디오의 시작을 0으로 본 상대 시간이다.</summary>
public readonly record struct TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>인터리브 float PCM 구간을 받아 텍스트로 전사한다.</summary>
public interface ISpeechRecognizer
{
    /// <param name="samples">인터리브된 float PCM 샘플(구간만).</param>
    /// <param name="sampleRate">원본 샘플레이트.</param>
    /// <param name="channels">원본 채널 수.</param>
    /// <param name="language">언어 코드(예: "ko"). 자동 감지는 "auto".</param>
    /// <param name="progress">모델 다운로드/진행 상태 알림.</param>
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples, int sampleRate, int channels,
        string language = "ko",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
