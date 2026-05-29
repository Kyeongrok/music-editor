using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfMusicEditor.Forms.Services;
using WpfMusicEditor.Main.Audio;
using WpfMusicEditor.Support.UI.Units;

namespace WpfMusicEditor.Forms.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IAudioEditor _editor;
    private readonly AudioPlayer _player;
    private readonly UpdateService _updateService;
    private readonly DispatcherTimer _cursorTimer;

    private AudioDocument? _document;
    private bool _suppressCursorSeek;
    private double _playbackEnd;

    public MainWindowViewModel(IAudioEditor editor, AudioPlayer player, UpdateService updateService)
    {
        _editor = editor;
        _player = player;
        _updateService = updateService;
        _player.PlaybackStopped += OnPlaybackStopped;

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _cursorTimer.Tick += OnCursorTick;
    }

    /// <summary>창 로드 시 호출. 새 버전이 있으면 내려받고 재시작 여부를 묻는다.</summary>
    public async Task CheckForUpdateAsync()
    {
        var newVersion = await _updateService.CheckForUpdateAsync();
        if (newVersion == null)
            return;

        Status = $"새 버전 {newVersion} 다운로드 중...";
        await _updateService.DownloadUpdateAsync(p => Status = $"업데이트 다운로드 중... {p}%");

        var result = MessageBox.Show(
            $"버전 {newVersion}으로 업데이트할 준비가 됐습니다.\n지금 재시작하시겠습니까?",
            "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
            _updateService.ApplyUpdateAndRestart();
        else
            Status = "업데이트 준비 완료 (다음 재시작 시 적용)";
    }

    public IReadOnlyList<AudioFormat> Formats { get; } = Enum.GetValues<AudioFormat>();

    [ObservableProperty]
    private string _fileName = "(열린 파일 없음)";

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    private double _startSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    private double _endSeconds;

    // 구간 볼륨을 키울/줄일 양(dB). 양수면 키우고 음수면 줄인다.
    [ObservableProperty]
    private double _gainDb = 6;

    [ObservableProperty]
    private AudioFormat _selectedFormat = AudioFormat.M4a;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "파일을 열고 파형을 드래그해 구간 선택 → 잘라내기 (Ctrl+Z 실행취소)";

    [ObservableProperty]
    private float[] _peaks = Array.Empty<float>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    private double _playPositionSeconds;

    [ObservableProperty]
    private WaveformInteractionMode _waveformMode = WaveformInteractionMode.Select;

    // 값을 바꾸면 파형이 전체 보기로 돌아간다(새 파일 열기 전용). 잘라내기/실행취소는 확대 유지.
    [ObservableProperty]
    private int _waveformResetView;

    public string PlayPauseLabel => IsPlaying ? "❚❚ 일시정지" : "▶ 재생";

    private bool HasDocument => _document is { FrameCount: > 0 };

    private const int WaveformResolution = 2000;

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "오디오 파일 열기",
            Filter = "오디오 파일 (*.m4a;*.mp4;*.aac;*.mp3;*.wav)|*.m4a;*.mp4;*.aac;*.mp3;*.wav|모든 파일 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        StopPlayback();
        IsBusy = true;
        try
        {
            Status = "불러오는 중...";
            var document = await _editor.LoadAsync(dialog.FileName);
            var peaks = await Task.Run(() => document.ComputePeaks(WaveformResolution));

            _document = document;
            FileName = Path.GetFileName(dialog.FileName);
            DurationSeconds = document.Duration.TotalSeconds;
            StartSeconds = 0;
            EndSeconds = document.Duration.TotalSeconds;
            Peaks = peaks;
            PlayPositionSeconds = 0;
            WaveformResetView++;
            _player.LoadSamples(document.Samples, document.SampleRate, document.Channels);

            Status = $"불러옴 · {document.Duration:mm\\:ss} · {document.SampleRate / 1000.0:0.#}kHz · {document.Channels}ch";
            RefreshCommands();
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

    private bool CanInteract() => !IsBusy;

    private bool CanPlay() => !IsBusy && HasDocument;

    // ── 편집: 구간 잘라내기 / 실행 취소 ─────────────────────────────

    private bool CanCut() => !IsBusy && HasDocument && EndSeconds > StartSeconds;

    [RelayCommand(CanExecute = nameof(CanCut))]
    private async Task CutAsync()
    {
        StopPlayback();
        IsBusy = true;
        try
        {
            var cutAt = StartSeconds;
            await Task.Run(() =>
                _document!.Cut(TimeSpan.FromSeconds(StartSeconds), TimeSpan.FromSeconds(EndSeconds)));
            await RefreshAfterEditAsync(cutAt);
            Status = $"잘라냄 · 남은 길이 {DurationSeconds:0.##}초 (Ctrl+Z로 실행 취소)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 편집: 구간 볼륨 조절 ─────────────────────────────────────

    private bool CanApplyGain() => !IsBusy && HasDocument && EndSeconds > StartSeconds;

    [RelayCommand(CanExecute = nameof(CanApplyGain))]
    private async Task ApplyGainAsync()
    {
        StopPlayback();
        IsBusy = true;
        try
        {
            var start = StartSeconds;
            var end = EndSeconds;
            var db = GainDb;
            await Task.Run(() =>
                _document!.ApplyGain(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), db));

            // 길이는 그대로이므로 다듬은 뒤 원래 구간 선택을 되살린다.
            await RefreshAfterEditAsync(start);
            StartSeconds = Math.Clamp(start, 0, DurationSeconds);
            EndSeconds = Math.Clamp(end, 0, DurationSeconds);

            Status = $"구간 볼륨 {db:+0.#;-0.#;0}dB 적용 · {start:0.##}~{end:0.##}초 (Ctrl+Z로 실행 취소)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUndo() => !IsBusy && _document?.CanUndo == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        StopPlayback();
        IsBusy = true;
        try
        {
            await Task.Run(() => _document!.Undo());
            await RefreshAfterEditAsync(StartSeconds);
            Status = $"실행 취소됨 · 길이 {DurationSeconds:0.##}초";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAfterEditAsync(double selectionPoint)
    {
        DurationSeconds = _document!.Duration.TotalSeconds;
        Peaks = await Task.Run(() => _document.ComputePeaks(WaveformResolution));
        _player.LoadSamples(_document.Samples, _document.SampleRate, _document.Channels);

        // 편집 후 선택은 잘라낸 지점에 접어 둔다(빈 선택).
        var point = Math.Clamp(selectionPoint, 0, DurationSeconds);
        StartSeconds = point;
        EndSeconds = point;
        PlayPositionSeconds = point;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        CutCommand.NotifyCanExecuteChanged();
        ApplyGainCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ── 재생 ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            _player.Pause();
            _cursorTimer.Stop();
            IsPlaying = false;
            return;
        }

        var from = PlayPositionSeconds;
        if (from < 0 || from >= DurationSeconds)
            from = StartSeconds;

        _playbackEnd = ResolvePlaybackEnd(from);
        _player.Play(TimeSpan.FromSeconds(from));
        _cursorTimer.Start();
        IsPlaying = true;
    }

    /// <summary>
    /// 재생 시작 지점이 선택 구간 안이면 구간 끝에서 멈추고,
    /// 구간 밖(파형의 다른 곳을 클릭)이면 끝까지 재생한다.
    /// </summary>
    private double ResolvePlaybackEnd(double from)
        => EndSeconds > StartSeconds && from >= StartSeconds && from < EndSeconds
            ? EndSeconds
            : DurationSeconds;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Stop() => StopPlayback();

    private void StopPlayback()
    {
        _cursorTimer.Stop();
        _player.Stop();
        IsPlaying = false;
        PlayPositionSeconds = StartSeconds;
    }

    private void OnCursorTick(object? sender, EventArgs e)
    {
        var pos = _player.CurrentTime.TotalSeconds;

        // 타이머가 쓰는 값은 재생기의 실제 위치이므로 다시 시킹하지 않는다.
        _suppressCursorSeek = true;
        PlayPositionSeconds = pos;
        _suppressCursorSeek = false;

        if (pos >= _playbackEnd)
            StopPlayback();
    }

    // 재생 중 파형을 클릭하면(= 외부에서 위치 변경) 그 지점으로 즉시 이동한다.
    partial void OnPlayPositionSecondsChanged(double value)
    {
        if (_suppressCursorSeek || !IsPlaying)
            return;

        _playbackEnd = ResolvePlaybackEnd(value);
        _player.Play(TimeSpan.FromSeconds(value));
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        if (IsPlaying)
        {
            _cursorTimer.Stop();
            IsPlaying = false;
        }
    }

    // ── 내보내기 ──────────────────────────────────────────────────

    private bool CanExport() => !IsBusy && HasDocument;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "내보내기",
            Filter = SelectedFormat.ToFilter(),
            FileName = $"{Path.GetFileNameWithoutExtension(FileName)}_edit{SelectedFormat.ToExtension()}"
        };
        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        Status = "내보내는 중...";
        try
        {
            await _editor.ExportAsync(_document!, dialog.FileName, SelectedFormat);
            Status = $"완료: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Status = $"내보내기 실패: {ex.Message}";
            MessageBox.Show(ex.Message, "내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
