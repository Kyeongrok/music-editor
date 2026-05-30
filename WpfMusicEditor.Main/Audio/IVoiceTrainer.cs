namespace WpfMusicEditor.Main.Audio;

/// <summary>학습 파라미터. 기본값은 RTX 3070(8GB)·40k v2 기준.</summary>
public sealed record TrainOptions(
    int Epochs = 150,
    int BatchSize = 7,
    string SampleRate = "40k",
    string Version = "v2");

/// <summary>
/// 타깃 목소리 데이터셋(wav 폴더)으로 RVC 모델을 학습해 <c>&lt;name&gt;.onnx</c>를 만든다.
/// 실제 학습은 외부 RVC(Python) 프로세스가 수행하며, 이 인터페이스는 그것을 구동·감시한다.
/// </summary>
public interface IVoiceTrainer
{
    /// <param name="modelName">만들 모델 이름(파일명/실험명). 영숫자·밑줄로 정규화된다.</param>
    /// <param name="datasetDir">타깃 목소리 wav들이 든 폴더.</param>
    /// <param name="options">학습 파라미터.</param>
    /// <param name="progress">단계/진행 상태 텍스트.</param>
    /// <param name="percentProgress">학습 진행률(0~100).</param>
    /// <returns>models 폴더에 등록된 <c>.onnx</c> 경로.</returns>
    Task<string> CreateModelAsync(
        string modelName, string datasetDir, TrainOptions options,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 학습 없이, 기존 RVC <c>.pth</c> 모델을 <c>.onnx</c>로 변환해 models 폴더에 등록한다.
    /// 받은 .pth 목소리를 바로 음색 변환에 쓸 수 있게 해 준다.
    /// </summary>
    /// <returns>models 폴더에 등록된 <c>.onnx</c> 경로.</returns>
    Task<string> ExportOnnxAsync(
        string pthPath, string modelName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
