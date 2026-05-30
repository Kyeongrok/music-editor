using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfMusicEditor.Main;
using WpfMusicEditor.Main.Audio;

namespace WpfMusicEditor.Forms.ViewModels;

/// <summary>
/// 타깃 목소리 데이터셋을 모아 RVC 학습을 구동하는 화면의 ViewModel.
/// 학습 자체는 외부 RVC(Python) 프로세스가 수행한다(<see cref="IVoiceTrainer"/>).
/// </summary>
public partial class TrainingViewModel : ObservableObject
{
    private readonly IAudioEditor _editor;
    private readonly IVoiceTrainer _trainer;
    private readonly AppSettings _settings;

    private AudioDocument? _source;
    private CancellationTokenSource? _cts;
    private int _clipIndex;
    private double _datasetSeconds;

    /// <summary>학습이 끝나 모델이 만들어지면 그 .onnx 경로를 알린다(메인 창이 받아 선택).</summary>
    public event Action<string>? ModelCreated;

    public TrainingViewModel(IAudioEditor editor, IVoiceTrainer trainer, AppSettings settings)
    {
        _editor = editor;
        _trainer = trainer;
        _settings = settings;
        _rvcRoot = settings.RvcRoot ?? "";
        _pythonPath = settings.PythonPath ?? "";
    }

    /// <summary>이번 세션의 데이터셋 폴더(누적 wav 저장).</summary>
    private string DatasetDir
    {
        get
        {
            var dir = Path.Combine(AppSettings.AppDataDir, "training", "dataset");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ── 설정(경로) ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _rvcRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _pythonPath;

    partial void OnRvcRootChanged(string value)
    {
        _settings.RvcRoot = value;
        _settings.Save();
    }

    partial void OnPythonPathChanged(string value)
    {
        _settings.PythonPath = value;
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseRvcRoot()
    {
        var dialog = new OpenFolderDialog { Title = "RVC 폴더 선택(런타임 python 포함)" };
        if (dialog.ShowDialog() == true)
        {
            RvcRoot = dialog.FolderName;
            // 흔한 위치의 python.exe를 자동 채움.
            if (string.IsNullOrEmpty(PythonPath))
            {
                foreach (var rel in new[] { "runtime\\python.exe", "venv\\Scripts\\python.exe" })
                {
                    var p = Path.Combine(dialog.FolderName, rel);
                    if (File.Exists(p)) { PythonPath = p; break; }
                }
            }
        }
    }

    [RelayCommand]
    private void BrowsePython()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Python 실행 파일 선택",
            Filter = "python.exe|python.exe|모든 파일 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            PythonPath = dialog.FileName;
    }

    // ── 데이터셋 빌더 ─────────────────────────────────────────────

    [ObservableProperty]
    private string _sourceFileName = "(파일 없음)";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddWholeFileCommand))]
    private double _sourceDurationSeconds;

    [ObservableProperty]
    private double _regionStartSeconds;

    [ObservableProperty]
    private double _regionEndSeconds;

    public ObservableCollection<string> DatasetClips { get; } = new();

    [ObservableProperty]
    private string _datasetSummary = "데이터셋 비어 있음";

    private bool HasSource => _source is { FrameCount: > 0 };

    [RelayCommand]
    private async Task LoadSourceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "학습용 오디오 불러오기",
            Filter = "오디오 파일 (*.m4a;*.mp4;*.aac;*.mp3;*.wav)|*.m4a;*.mp4;*.aac;*.mp3;*.wav|모든 파일 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _source = await _editor.LoadAsync(dialog.FileName);
            SourceFileName = Path.GetFileName(dialog.FileName);
            SourceDurationSeconds = _source.Duration.TotalSeconds;
            RegionStartSeconds = 0;
            RegionEndSeconds = SourceDurationSeconds;
            AddRegionCommand.NotifyCanExecuteChanged();
            AddWholeFileCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "불러오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanAddRegion() => HasSource && RegionEndSeconds > RegionStartSeconds;

    [RelayCommand(CanExecute = nameof(CanAddRegion))]
    private void AddRegion() => AddClip(RegionStartSeconds, RegionEndSeconds);

    private bool CanAddWhole() => HasSource;

    [RelayCommand(CanExecute = nameof(CanAddWhole))]
    private void AddWholeFile() => AddClip(0, SourceDurationSeconds);

    private void AddClip(double startSec, double endSec)
    {
        if (_source is null)
            return;

        var region = _source.CopyRange(TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec));
        if (region.Length == 0)
            return;

        _clipIndex++;
        var clipPath = Path.Combine(DatasetDir, $"clip_{_clipIndex:D4}.wav");
        DatasetWriter.WriteMonoWav(clipPath, region, _source.SampleRate, _source.Channels);

        var seconds = endSec - startSec;
        _datasetSeconds += seconds;
        DatasetClips.Add($"{Path.GetFileName(clipPath)} · {seconds:0.#}초");
        UpdateDatasetSummary();
        StartTrainingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearDataset()
    {
        try
        {
            if (Directory.Exists(DatasetDir))
                Directory.Delete(DatasetDir, recursive: true);
        }
        catch { /* 사용 중 등 무시 */ }

        DatasetClips.Clear();
        _datasetSeconds = 0;
        _clipIndex = 0;
        UpdateDatasetSummary();
        StartTrainingCommand.NotifyCanExecuteChanged();
    }

    private void UpdateDatasetSummary() =>
        DatasetSummary = DatasetClips.Count == 0
            ? "데이터셋 비어 있음"
            : $"클립 {DatasetClips.Count}개 · 총 {_datasetSeconds / 60.0:0.#}분";

    // ── 파라미터 + 학습 ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _modelName = "my_voice";

    [ObservableProperty]
    private int _epochs = 150;

    [ObservableProperty]
    private int _batchSize = 7;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTrainingCommand))]
    private bool _isTraining;

    [ObservableProperty]
    private double _trainProgress;

    [ObservableProperty]
    private string _status = "데이터셋을 모으고 RVC 경로를 지정한 뒤 학습을 시작하세요.";

    private bool CanStartTraining() =>
        !IsTraining
        && DatasetClips.Count > 0
        && !string.IsNullOrWhiteSpace(ModelName)
        && !string.IsNullOrWhiteSpace(RvcRoot)
        && !string.IsNullOrWhiteSpace(PythonPath);

    [RelayCommand(CanExecute = nameof(CanStartTraining))]
    private async Task StartTrainingAsync()
    {
        IsTraining = true;
        TrainProgress = 0;
        _cts = new CancellationTokenSource();
        try
        {
            var options = new TrainOptions(Epochs, BatchSize);
            var progress = new Progress<string>(s => Status = s);
            var percent = new Progress<double>(p => TrainProgress = p);

            var path = await _trainer.CreateModelAsync(
                ModelName, DatasetDir, options, progress, percent, _cts.Token);

            Status = $"학습 완료 · {Path.GetFileName(path)} (모델 목록에 등록됨)";
            ModelCreated?.Invoke(path);
            MessageBox.Show($"모델이 만들어졌습니다:\n{path}\n\n메인 창에서 바로 음색 변환에 쓸 수 있습니다.",
                "학습 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Status = "학습이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            Status = $"학습 실패: {ex.Message}";
            MessageBox.Show(ex.Message, "학습 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsTraining = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancelTraining() => IsTraining;

    [RelayCommand(CanExecute = nameof(CanCancelTraining))]
    private void CancelTraining()
    {
        Status = "학습 취소 중...";
        _cts?.Cancel();
    }
}
