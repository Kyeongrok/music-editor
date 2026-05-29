using System.Collections;
using System.Collections.ObjectModel;
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
    private readonly ISpeechRecognizer _recognizer;
    private readonly AudioPlayer _player;
    private readonly UpdateService _updateService;
    private readonly DispatcherTimer _cursorTimer;

    private AudioDocument? _document;
    private bool _suppressCursorSeek;
    private double _playbackEnd;

    public MainWindowViewModel(IAudioEditor editor, ISpeechRecognizer recognizer,
        AudioPlayer player, UpdateService updateService)
    {
        _editor = editor;
        _recognizer = recognizer;
        _player = player;
        _updateService = updateService;
        _player.PlaybackStopped += OnPlaybackStopped;

        // NVIDIA/CUDA GPU가 없으면 STT가 CPU로 동작해 매우 느리므로 상단에 경고를 띄운다.
        ShowGpuWarning = !HasCudaGpu();

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _cursorTimer.Tick += OnCursorTick;

        // 줄 개수가 바뀌면 지우기/이동 버튼의 활성화 상태를 갱신한다.
        TranscriptLines.CollectionChanged += (_, _) =>
        {
            ClearTranscriptCommand.NotifyCanExecuteChanged();
            MoveLinesUpCommand.NotifyCanExecuteChanged();
            MoveLinesDownCommand.NotifyCanExecuteChanged();
        };
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

    /// <summary>NVIDIA/CUDA GPU가 없어 STT가 CPU로만 동작할 때 true. 상단 경고 배너에 바인딩된다.</summary>
    public bool ShowGpuWarning { get; }

    // NVIDIA 드라이버가 깔리면 System32에 nvcuda.dll이 생긴다. 이를 GPU 가용성의 가벼운 판단 기준으로 쓴다.
    private static bool HasCudaGpu()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AudioFormat> Formats { get; } = Enum.GetValues<AudioFormat>();

    [ObservableProperty]
    private string _fileName = "(열린 파일 없음)";

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    private double _startSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    private double _endSeconds;

    // 구간 볼륨을 키울/줄일 양(dB). 양수면 키우고 음수면 줄인다.
    [ObservableProperty]
    private double _gainDb = 6;

    // 선택 구간 전사 결과(타임스탬프 포함). 전사할 때마다 줄 단위로 누적되며
    // 목록에서 여러 줄을 선택해 ↑↓로 순서를 바꿀 수 있다.
    public ObservableCollection<TranscriptLine> TranscriptLines { get; } = new();

    // 전사는 시간이 걸리므로 IsBusy와 분리한다. 전사 중에도 재생은 허용하고
    // 샘플을 바꾸는 편집/내보내기/열기만 막는다.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isTranscribing;

    // 전사 진행률(0~100). 프로그레스바에 바인딩된다.
    [ObservableProperty]
    private double _transcribeProgress;

    [ObservableProperty]
    private AudioFormat _selectedFormat = AudioFormat.M4a;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGainCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
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

    private bool CanInteract() => !IsBusy && !IsTranscribing;

    private bool CanPlay() => !IsBusy && HasDocument;

    // ── 편집: 구간 잘라내기 / 실행 취소 ─────────────────────────────

    private bool CanCut() => !IsBusy && !IsTranscribing && HasDocument && EndSeconds > StartSeconds;

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

    private bool CanApplyGain() => !IsBusy && !IsTranscribing && HasDocument && EndSeconds > StartSeconds;

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

    // ── 음성 인식: 선택 구간 전사 ─────────────────────────────────

    private bool CanTranscribe() => !IsBusy && !IsTranscribing && HasDocument && EndSeconds > StartSeconds;

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        // 재생은 멈추지 않는다. 전사 동안에도 들을 수 있게 한다.
        IsTranscribing = true;
        TranscribeProgress = 0;
        var start = StartSeconds;
        var end = EndSeconds;
        try
        {
            var region = _document!.CopyRange(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
            var sampleRate = _document.SampleRate;
            var channels = _document.Channels;

            // 모델 로딩·리샘플·추론은 무거우므로 백그라운드에서 돌려 UI(재생 포함)를 막지 않는다.
            var progress = new Progress<string>(s => Status = s);
            var percent = new Progress<double>(p => TranscribeProgress = p);
            var segments = await Task.Run(
                () => _recognizer.TranscribeAsync(region, sampleRate, channels, "ko", progress, percent));

            // 타임스탬프는 구간 시작을 0으로 본 상대값이라 파일 기준 절대 시간으로 보정한다.
            // 이전 결과를 지우지 않고 줄 단위로 이어 붙인다.
            var offset = TimeSpan.FromSeconds(start);
            foreach (var s in segments)
                TranscriptLines.Add(new TranscriptLine($"[{s.Start + offset:mm\\:ss}] {s.Text}"));

            Status = segments.Count == 0
                ? "전사 완료 · 인식된 음성 없음"
                : $"전사 완료 · {segments.Count}개 구간 ({start:0.##}~{end:0.##}초)";
        }
        catch (Exception ex)
        {
            Status = $"전사 실패: {ex.Message}";
            MessageBox.Show(ex.Message, "전사 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsTranscribing = false;
        }
    }

    private bool HasTranscript() => TranscriptLines.Count > 0;

    [RelayCommand(CanExecute = nameof(HasTranscript))]
    private void ClearTranscript() => TranscriptLines.Clear();

    [RelayCommand(CanExecute = nameof(HasTranscript))]
    private void MoveLinesUp(IList? selected) => MoveLines(selected, -1);

    [RelayCommand(CanExecute = nameof(HasTranscript))]
    private void MoveLinesDown(IList? selected) => MoveLines(selected, +1);

    /// <summary>선택된 줄들을 위(-1)/아래(+1)로 한 칸 이동한다. 선택은 그대로 따라간다.</summary>
    private void MoveLines(IList? selected, int delta)
    {
        if (selected is null || selected.Count == 0)
            return;

        // 실제 컬렉션에서의 인덱스를 참조로 찾아 정렬한다(중복 텍스트도 안전).
        var indices = selected.Cast<TranscriptLine>()
            .Select(line => TranscriptLines.IndexOf(line))
            .Where(i => i >= 0)
            .OrderBy(i => i)
            .ToList();
        if (indices.Count == 0)
            return;

        if (delta < 0)
        {
            if (indices[0] == 0)
                return; // 이미 맨 위
            foreach (var idx in indices)
                TranscriptLines.Move(idx, idx - 1);
        }
        else
        {
            if (indices[^1] == TranscriptLines.Count - 1)
                return; // 이미 맨 아래
            for (var i = indices.Count - 1; i >= 0; i--)
                TranscriptLines.Move(indices[i], indices[i] + 1);
        }
    }

    private bool CanUndo() => !IsBusy && !IsTranscribing && _document?.CanUndo == true;

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
        TranscribeCommand.NotifyCanExecuteChanged();
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
    /// 선택 구간과 무관하게 항상 파일 끝까지 재생한다.
    /// </summary>
    private double ResolvePlaybackEnd(double from) => DurationSeconds;

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

    private bool CanExport() => !IsBusy && !IsTranscribing && HasDocument;

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

/// <summary>전사 결과의 한 줄. 중복 텍스트도 구분되도록 참조 타입으로 둔다.</summary>
public sealed class TranscriptLine
{
    public TranscriptLine(string text) => Text = text;

    public string Text { get; }
}
