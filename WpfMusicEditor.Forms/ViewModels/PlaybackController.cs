using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfMusicEditor.Main.Audio;

namespace WpfMusicEditor.Forms.ViewModels;

/// <summary>
/// 파형 미리듣기 재생(재생/일시정지/정지, 커서 위치 추적)을 담당하는 공용 컨트롤러.
/// MainWindowViewModel과 InsertAudioViewModel이 각자의 <see cref="AudioPlayer"/>를 감싸 구성(composition)한다.
/// </summary>
public partial class PlaybackController : ObservableObject, IDisposable
{
    private readonly AudioPlayer _player;
    private readonly DispatcherTimer _cursorTimer;
    private readonly Func<double> _resolveDurationSeconds;
    private readonly Func<double> _resolveResetSeconds;
    private bool _suppressCursorSeek;

    /// <param name="player">재생에 쓸 오디오 플레이어(소유권은 호출자에게 있다 — Dispose는 이 컨트롤러가 하지 않는다).</param>
    /// <param name="resolveDurationSeconds">재생을 자동 정지할 길이(초)를 돌려준다.</param>
    /// <param name="resolveResetSeconds">정지 시/재생 위치가 유효 범위를 벗어났을 때 되돌아갈 위치(초)를 돌려준다.</param>
    public PlaybackController(AudioPlayer player, Func<double> resolveDurationSeconds, Func<double> resolveResetSeconds)
    {
        _player = player;
        _resolveDurationSeconds = resolveDurationSeconds;
        _resolveResetSeconds = resolveResetSeconds;
        _player.PlaybackStopped += OnPlaybackStopped;
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _cursorTimer.Tick += OnCursorTick;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPositionLabel))]
    private double _playPositionSeconds;

    public string PlayPauseLabel => IsPlaying ? "❚❚ 일시정지" : "▶ 재생";

    /// <summary>재생 위치(노란 바)를 분:초 형태로 표시한다.</summary>
    public string PlayPositionLabel => TimeSpan.FromSeconds(Math.Max(0, PlayPositionSeconds)).ToString(@"mm\:ss");

    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            _player.Pause();
            _cursorTimer.Stop();
            IsPlaying = false;
            return;
        }

        var from = PlayPositionSeconds;
        if (from < 0 || from >= _resolveDurationSeconds())
            from = _resolveResetSeconds();

        _player.Play(TimeSpan.FromSeconds(from));
        _cursorTimer.Start();
        IsPlaying = true;
    }

    public void Stop()
    {
        _cursorTimer.Stop();
        _player.Stop();
        IsPlaying = false;
        PlayPositionSeconds = _resolveResetSeconds();
    }

    private void OnCursorTick(object? sender, EventArgs e)
    {
        var pos = _player.CurrentTime.TotalSeconds;

        // 타이머가 쓰는 값은 재생기의 실제 위치이므로 다시 시킹하지 않는다.
        _suppressCursorSeek = true;
        PlayPositionSeconds = pos;
        _suppressCursorSeek = false;

        if (pos >= _resolveDurationSeconds())
            Stop();
    }

    // 재생 중 파형을 클릭하면(= 외부에서 위치 변경) 그 지점으로 즉시 이동한다.
    partial void OnPlayPositionSecondsChanged(double value)
    {
        if (_suppressCursorSeek || !IsPlaying)
            return;

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

    public void Dispose()
    {
        _cursorTimer.Stop();
        _player.PlaybackStopped -= OnPlaybackStopped;
    }
}
