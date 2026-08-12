using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfMusicEditor.Main.Audio;
using WpfMusicEditor.Support.UI.Units;

namespace WpfMusicEditor.Forms.ViewModels;

/// <summary>
/// 다른 오디오 파일을 열어 파형에서 가져올 구간을 고르는 삽입 대화상자의 뷰모델.
/// 미리듣기는 메인과 충돌하지 않도록 전용 <see cref="AudioPlayer"/>를 따로 쓴다.
/// </summary>
public partial class InsertAudioViewModel : ObservableObject, IDisposable
{
    private readonly IAudioEditor _editor;
    private readonly AudioPlayer _player = new();

    private AudioDocument? _source;

    public InsertAudioViewModel(IAudioEditor editor)
    {
        _editor = editor;
        Playback = new PlaybackController(_player, () => DurationSeconds, () => StartSeconds);
    }

    public PlaybackController Playback { get; }

    [ObservableProperty]
    private string _fileName = "(가져올 파일을 여세요)";

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private double _startSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private double _endSeconds;

    [ObservableProperty]
    private float[] _peaks = Array.Empty<float>();

    [ObservableProperty]
    private WaveformInteractionMode _waveformMode = WaveformInteractionMode.Select;

    // 값을 바꾸면 파형이 전체 보기로 돌아간다(새 파일 열기 시 증가).
    [ObservableProperty]
    private int _waveformResetView;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "파일을 열고 파형을 드래그해 가져올 구간을 선택하세요.";

    /// <summary>파일이 열려 있고 유효한 구간이 선택돼야 삽입할 수 있다.</summary>
    public bool CanConfirm => HasDocument && EndSeconds > StartSeconds;

    private bool HasDocument => _source is { FrameCount: > 0 };

    private const int WaveformResolution = 2000;

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "삽입할 오디오 파일 열기",
            Filter = "오디오 파일 (*.m4a;*.mp4;*.aac;*.mp3;*.wav)|*.m4a;*.mp4;*.aac;*.mp3;*.wav|모든 파일 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        Playback.Stop();
        IsBusy = true;
        try
        {
            Status = "불러오는 중...";
            var document = await _editor.LoadAsync(dialog.FileName);
            var peaks = await Task.Run(() => document.ComputePeaks(WaveformResolution));

            _source = document;
            FileName = Path.GetFileName(dialog.FileName);
            DurationSeconds = document.Duration.TotalSeconds;
            StartSeconds = 0;
            EndSeconds = document.Duration.TotalSeconds;
            Peaks = peaks;
            Playback.PlayPositionSeconds = 0;
            WaveformResetView++;
            _player.LoadSamples(document.Samples, document.SampleRate, document.Channels);

            Status = $"불러옴 · {document.Duration:mm\\:ss} · {document.SampleRate / 1000.0:0.#}kHz · {document.Channels}ch · 드래그로 구간 선택";
            OnPropertyChanged(nameof(CanConfirm));
        }
        catch (Exception ex)
        {
            Status = $"파일을 열지 못했습니다: {ex.Message}";
            MessageBox.Show(ex.Message, "열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasDocument)
            return;

        Playback.TogglePlayPause();
    }

    [RelayCommand]
    private void Stop() => Playback.Stop();

    /// <summary>선택 구간의 원본 인터리브 샘플과 원본 샘플레이트·채널을 돌려준다(원본은 그대로).</summary>
    public (float[] region, int sampleRate, int channels) GetSelectedRegion()
    {
        if (!HasDocument)
            return (Array.Empty<float>(), 0, 0);

        var region = _source!.CopyRange(TimeSpan.FromSeconds(StartSeconds), TimeSpan.FromSeconds(EndSeconds));
        return (region, _source.SampleRate, _source.Channels);
    }

    public void Dispose()
    {
        Playback.Dispose();
        _player.Dispose();
    }
}
