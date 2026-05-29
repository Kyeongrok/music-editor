using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfMusicEditor.Support.UI.Units;

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
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

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
                ctx.BeginFigure(new Point(0, center), true, true);
                // 위쪽 윤곽 (왼 → 오)
                for (var i = 0; i < peaks.Count; i++)
                {
                    var x = i / (double)divisor * width;
                    ctx.LineTo(new Point(x, center - peaks[i] * center), false, false);
                }
                // 아래쪽 윤곽 (오 → 왼), 대칭으로 채움
                for (var i = peaks.Count - 1; i >= 0; i--)
                {
                    var x = i / (double)divisor * width;
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
        _dragAnchorSeconds = XToSeconds(_downPoint.X);
        _isDragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
            return;

        var p = e.GetPosition(this);
        if (!_isDragging && Math.Abs(p.X - _downPoint.X) >= DragThreshold)
            _isDragging = true;

        if (_isDragging)
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

    private double SecondsToX(double seconds, double width)
        => Duration <= 0 ? 0 : Math.Clamp(seconds / Duration, 0, 1) * width;

    private double XToSeconds(double x)
    {
        if (ActualWidth <= 0)
            return 0;
        var ratio = Math.Clamp(x / ActualWidth, 0, 1);
        return ratio * Duration;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
