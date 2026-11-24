using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BekkyWake.UI;

public partial class HoldToEndButton : System.Windows.Controls.UserControl
{
    private const double HoldDurationSeconds = 0.6;
    private const double CancelDistancePixels = 12.0;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _watch = new();
    private bool _holding;
    private System.Windows.Point _startPoint;

    public event Action? HoldCompleted;

    public HoldToEndButton()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;

        MouseEnter += (_, _) => HoverFill.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 255, 255, 255));
        MouseLeave += (_, _) =>
        {
            HoverFill.Fill = System.Windows.Media.Brushes.Transparent;
            if (_holding) CancelHold();
        };
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += (_, _) => CancelHold();
        MouseMove += OnMove;
        LostMouseCapture += (_, _) => CancelHold();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelHold(); };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true;
        _holding = true;
        _startPoint = e.GetPosition(this);
        CaptureMouse();
        ScaleTx.ScaleX = 1.10;
        ScaleTx.ScaleY = 1.10;
        _watch.Restart();
        _timer.Start();
        UpdateArc(0.01);
    }

    private void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_holding) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _startPoint.X;
        var dy = pos.Y - _startPoint.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > CancelDistancePixels)
        {
            CancelHold();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_holding) return;
        var progress = _watch.Elapsed.TotalSeconds / HoldDurationSeconds;
        if (progress >= 1.0)
        {
            CompleteHold();
        }
        else
        {
            UpdateArc(progress);
        }
    }

    private void CompleteHold()
    {
        CancelHold();
        HoldCompleted?.Invoke();
    }

    private void CancelHold()
    {
        if (!_holding) return;
        _holding = false;
        _timer.Stop();
        _watch.Reset();
        if (IsMouseCaptured) ReleaseMouseCapture();
        ScaleTx.ScaleX = 1.0;
        ScaleTx.ScaleY = 1.0;
        ProgressArc.Data = null;
    }

    private void UpdateArc(double progress)
    {
        const double cx = 12.0;
        const double cy = 12.0;
        const double r = 10.0;

        var clamped = Math.Clamp(progress, 0.0, 0.999);
        var angleRad = (-90.0 + clamped * 360.0) * Math.PI / 180.0;
        var start = new System.Windows.Point(cx, cy - r);
        var end = new System.Windows.Point(cx + r * Math.Cos(angleRad), cy + r * Math.Sin(angleRad));
        var isLargeArc = clamped > 0.5;

        var seg = new ArcSegment(end, new System.Windows.Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, true);
        var fig = new PathFigure(start, [seg], false);
        ProgressArc.Data = new PathGeometry([fig]);
    }
}
