using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfMusicEditor.Support.UI.Units;

/// <summary>파형 위에서 마우스 드래그가 하는 일.</summary>
public enum WaveformInteractionMode
{
    /// <summary>드래그로 구간을 선택한다.</summary>
    Select,

    /// <summary>드래그로 보이는 구간을 좌우로 이동(팬)한다.</summary>
    Pan
}

/// <summary>
/// 피크 배열을 채워진 파형으로 그리고, 마우스 드래그로 구간(<see cref="SelectionStart"/>~
/// <see cref="SelectionEnd"/>, 단위: 초)을 선택할 수 있는 컨트롤.
/// </summary>
public class WaveformControl : Control
{
    private static readonly Brush DefaultBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));
    private static readonly Brush DefaultWave = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xD6)));
    private static readonly Brush DefaultSelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD4)));
    private static readonly Pen DefaultSelectionEdge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), 1.5));
    private static readonly Pen CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1));
    private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC0, 0x4D)), 1.5));

    private const double DragThreshold = 4;
    private double _dragAnchorSeconds;
    private Point _downPoint;
    private bool _isDragging;

    // 휠로 확대/축소한 결과 화면에 보이는 시간 구간(초). _viewEnd <= 0 이면 아직 초기화 전.
    private const double MinViewSeconds = 0.05;
    private const double ZoomStep = 1.25;
    private double _viewStart;
    private double _viewEnd;
    private double _panAnchorViewStart;
    private bool _syncingView;

    public WaveformControl()
    {
        // 확대 시 구간이 컨트롤 밖으로 그려지지 않도록 잘라낸다.
        ClipToBounds = true;
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(WaveformInteractionMode), typeof(WaveformControl),
            new FrameworkPropertyMetadata(WaveformInteractionMode.Select, OnModeChanged));

    /// <summary>드래그 동작 모드(선택/이동).</summary>
    public WaveformInteractionMode Mode
    {
        get => (WaveformInteractionMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (WaveformControl)d;
        control.Cursor = (WaveformInteractionMode)e.NewValue == WaveformInteractionMode.Pan
            ? Cursors.SizeAll
            : Cursors.Arrow;
    }

    public static readonly DependencyProperty PeaksProperty =
        DependencyProperty.Register(nameof(Peaks), typeof(IReadOnlyList<float>), typeof(WaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float>? Peaks
    {
        get => (IReadOnlyList<float>?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnDurationChanged));

    private static void OnDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 길이가 바뀌어도(잘라내기·실행취소) 확대 상태는 유지하고 범위만 새 길이에 맞춘다.
        // 새 파일을 열어 전체 보기로 되돌리는 건 ResetView 신호가 담당한다.
        ((WaveformControl)d).ClampViewToDuration((double)e.NewValue);
    }

    private void ClampViewToDuration(double duration)
    {
        if (duration <= 0)
        {
            _viewStart = 0;
            _viewEnd = 0;
        }
        else
        {
            var span = _viewEnd - _viewStart;
            if (span <= 0 || _viewEnd <= 0 || span >= duration)
            {
                // 미초기화이거나 전체 보기 → 전체로.
                _viewStart = 0;
                _viewEnd = duration;
            }
            else
            {
                // 확대 배율(span)은 유지하고 시작점만 [0, duration-span] 안으로 민다.
                span = Math.Min(span, duration);
                _viewStart = Math.Clamp(_viewStart, 0, duration - span);
                _viewEnd = _viewStart + span;
            }
        }

        SyncViewProperties();
    }

    public static readonly DependencyProperty ResetViewProperty =
        DependencyProperty.Register(nameof(ResetView), typeof(int), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0, OnResetViewChanged));

    /// <summary>값이 바뀔 때마다 전체 보기로 되돌린다(새 파일 열기 시 증가시켜 사용).</summary>
    public int ResetView
    {
        get => (int)GetValue(ResetViewProperty);
        set => SetValue(ResetViewProperty, value);
    }

    private static void OnResetViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (WaveformControl)d;
        control._viewStart = 0;
        control._viewEnd = control.Duration;
        control.SyncViewProperties();
        control.InvalidateVisual();
    }

    // ── 슬라이더 연동: 스크롤 위치(ViewStart)와 최대 스크롤(MaxViewStart) 노출 ──

    public static readonly DependencyProperty ViewStartProperty =
        DependencyProperty.Register(nameof(ViewStart), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0, OnViewStartChanged));

    /// <summary>보이는 구간의 시작(초). 슬라이더로 양방향 바인딩해 좌우 스크롤한다.</summary>
    public double ViewStart
    {
        get => (double)GetValue(ViewStartProperty);
        set => SetValue(ViewStartProperty, value);
    }

    public static readonly DependencyProperty MaxViewStartProperty =
        DependencyProperty.Register(nameof(MaxViewStart), typeof(double), typeof(WaveformControl),
            new PropertyMetadata(0.0));

    /// <summary>스크롤 가능한 최대 시작값(= Duration - 보이는 폭). 슬라이더의 Maximum 으로 쓴다.</summary>
    public double MaxViewStart
    {
        get => (double)GetValue(MaxViewStartProperty);
        private set => SetValue(MaxViewStartProperty, value);
    }

    private static void OnViewStartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (WaveformControl)d;
        if (control._syncingView)
            return; // 내부에서 동기화 중 → 무시.

        // 슬라이더 등 외부에서 스크롤 위치 변경: 확대 폭은 유지하고 시작만 이동.
        var (_, span) = control.GetView();
        var start = Math.Clamp((double)e.NewValue, 0, Math.Max(0, control.Duration - span));
        control._viewStart = start;
        control._viewEnd = start + span;
        control.SyncViewProperties();
        control.InvalidateVisual();
    }

    /// <summary>내부 _viewStart/_viewEnd 를 외부 노출 속성에 반영한다.</summary>
    private void SyncViewProperties()
    {
        _syncingView = true;
        var span = _viewEnd - _viewStart;
        MaxViewStart = Math.Max(0, Duration - span);
        ViewStart = _viewStart;
        _syncingView = false;
    }

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty SelectionStartProperty =
        DependencyProperty.Register(nameof(SelectionStart), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double SelectionStart
    {
        get => (double)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public static readonly DependencyProperty SelectionEndProperty =
        DependencyProperty.Register(nameof(SelectionEnd), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double SelectionEnd
    {
        get => (double)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public static readonly DependencyProperty WaveBrushProperty =
        DependencyProperty.Register(nameof(WaveBrush), typeof(Brush), typeof(WaveformControl),
            new FrameworkPropertyMetadata(DefaultWave, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush WaveBrush
    {
        get => (Brush)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public static readonly DependencyProperty PlayPositionProperty =
        DependencyProperty.Register(nameof(PlayPosition), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>재생 위치(초). 0 이하이거나 범위를 벗어나면 커서를 그리지 않는다.</summary>
    public double PlayPosition
    {
        get => (double)GetValue(PlayPositionProperty);
        set => SetValue(PlayPositionProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var bounds = new Rect(0, 0, width, height);
        dc.DrawRectangle(Background ?? DefaultBackground, null, bounds);

        var center = height / 2.0;
        dc.DrawLine(CenterPen, new Point(0, center), new Point(width, center));

        var peaks = Peaks;
        if (peaks is { Count: > 0 })
        {
            var divisor = peaks.Count - 1 == 0 ? 1 : peaks.Count - 1;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                // 피크 i 는 전체 길이 중 i/divisor 지점에 해당하므로,
                // 현재 보이는 구간 기준으로 x 좌표를 환산한다(확대 시 화면 밖은 클리핑).
                ctx.BeginFigure(new Point(SecondsToX(0, width), center), true, true);
                // 위쪽 윤곽 (왼 → 오)
                for (var i = 0; i < peaks.Count; i++)
                {
                    var x = SecondsToX(i / (double)divisor * Duration, width);
                    ctx.LineTo(new Point(x, center - peaks[i] * center), false, false);
                }
                // 아래쪽 윤곽 (오 → 왼), 대칭으로 채움
                for (var i = peaks.Count - 1; i >= 0; i--)
                {
                    var x = SecondsToX(i / (double)divisor * Duration, width);
                    ctx.LineTo(new Point(x, center + peaks[i] * center), false, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(WaveBrush, null, geometry);
        }

        // 선택 구간
        if (Duration > 0 && SelectionEnd > SelectionStart)
        {
            var x1 = SecondsToX(SelectionStart, width);
            var x2 = SecondsToX(SelectionEnd, width);
            var rect = new Rect(x1, 0, Math.Max(0, x2 - x1), height);
            dc.DrawRectangle(DefaultSelectionFill, null, rect);
            dc.DrawLine(DefaultSelectionEdge, new Point(x1, 0), new Point(x1, height));
            dc.DrawLine(DefaultSelectionEdge, new Point(x2, 0), new Point(x2, height));
        }

        // 재생 위치 커서
        if (Duration > 0 && PlayPosition >= 0 && PlayPosition <= Duration)
        {
            var px = SecondsToX(PlayPosition, width);
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0)
            return;

        CaptureMouse();
        _downPoint = e.GetPosition(this);
        _isDragging = false;

        if (Mode == WaveformInteractionMode.Pan)
            _panAnchorViewStart = GetView().start;
        else
            _dragAnchorSeconds = XToSeconds(_downPoint.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
            return;

        var p = e.GetPosition(this);
        if (!_isDragging && Math.Abs(p.X - _downPoint.X) >= DragThreshold)
            _isDragging = true;

        if (!_isDragging)
            return;

        if (Mode == WaveformInteractionMode.Pan)
        {
            // 드래그 방향으로 내용이 따라오도록 보이는 구간을 반대로 민다.
            var (_, span) = GetView();
            var dxSeconds = (p.X - _downPoint.X) / ActualWidth * span;
            var newStart = Math.Clamp(_panAnchorViewStart - dxSeconds, 0, Math.Max(0, Duration - span));
            _viewStart = newStart;
            _viewEnd = newStart + span;
            SyncViewProperties();
            InvalidateVisual();
        }
        else
        {
            var t = XToSeconds(p.X);
            SelectionStart = Math.Min(_dragAnchorSeconds, t);
            SelectionEnd = Math.Max(_dragAnchorSeconds, t);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();

        // 움직임 없이 누르고 뗐으면 클릭 → 재생 위치 점프 (구간은 유지).
        if (!_isDragging)
            PlayPosition = XToSeconds(_downPoint.X);

        _isDragging = false;
    }

    /// <summary>현재 보이는 구간을 [0, Duration] 범위로 보정해 반환한다.</summary>
    private (double start, double span) GetView()
    {
        var span = _viewEnd - _viewStart;
        if (span <= 0 || _viewEnd <= 0 || _viewEnd > Duration)
        {
            // 미초기화 또는 범위 이탈 → 전체 보기.
            _viewStart = 0;
            _viewEnd = Duration;
            span = Duration;
        }
        return (_viewStart, span);
    }

    private double SecondsToX(double seconds, double width)
    {
        if (Duration <= 0)
            return 0;
        var (start, span) = GetView();
        return (seconds - start) / span * width;
    }

    private double XToSeconds(double x)
    {
        if (ActualWidth <= 0 || Duration <= 0)
            return 0;
        var (start, span) = GetView();
        return Math.Clamp(start + x / ActualWidth * span, 0, Duration);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Duration <= 0 || ActualWidth <= 0)
            return;

        // Ctrl+휠 = 확대/축소, 그냥 휠 = 좌우 스크롤.
        // Keyboard.Modifiers 는 WPF 키보드 포커스에 의존해 휠만 굴릴 때 놓칠 수 있어
        // OS 키 상태를 직접 읽는다.
        if (IsKeyDown(VkControl))
            ZoomAt(e.GetPosition(this).X, e.Delta);
        else
            PanByWheel(e.Delta);

        e.Handled = true;
    }

    private const int VkControl = 0x11;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    private void ZoomAt(double x, int delta)
    {
        var (start, span) = GetView();
        // 휠 위 = 확대(구간 축소), 휠 아래 = 축소(구간 확장).
        var factor = delta > 0 ? 1.0 / ZoomStep : ZoomStep;
        var newSpan = Math.Clamp(span * factor, Math.Min(Duration, MinViewSeconds), Duration);

        // 커서 아래의 시각을 고정점으로 삼아 같은 화면 위치를 유지한다.
        var anchor = start + x / ActualWidth * span;
        var ratio = (anchor - start) / span;
        var newStart = Math.Clamp(anchor - ratio * newSpan, 0, Duration - newSpan);

        _viewStart = newStart;
        _viewEnd = newStart + newSpan;
        SyncViewProperties();
        InvalidateVisual();
    }

    private void PanByWheel(int delta)
    {
        var (start, span) = GetView();
        // 휠 위 = 왼쪽(앞)으로, 휠 아래 = 오른쪽(뒤)으로. 한 칸당 보이는 폭의 15%.
        var step = span * 0.15 * (delta > 0 ? -1 : 1);
        var newStart = Math.Clamp(start + step, 0, Math.Max(0, Duration - span));

        _viewStart = newStart;
        _viewEnd = newStart + span;
        SyncViewProperties();
        InvalidateVisual();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
