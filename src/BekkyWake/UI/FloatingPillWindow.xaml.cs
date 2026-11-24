using System.Windows;
using System.Windows.Input;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.UI;

public partial class FloatingPillWindow : Window
{
    private readonly SessionManager _manager;
    private readonly IClock _clock;
    private readonly Action<System.Drawing.Point> _openPopoverAt;

    private bool _dragging;
    private System.Windows.Point _mouseDownScreen;

    public FloatingPillWindow(
        SessionManager manager,
        IClock clock,
        Action<System.Drawing.Point> openPopoverAt)
    {
        InitializeComponent();
        _manager = manager;
        _clock = clock;
        _openPopoverAt = openPopoverAt;

        PillHoldToEnd.HoldCompleted += async () => await _manager.EndAsync(EndReason.User);

        PillBorder.MouseLeftButtonDown += OnMouseDown;
        PillBorder.MouseMove += OnMouseMove;
        PillBorder.MouseLeftButtonUp += OnMouseUp;
    }

    public void ApplyPositionFromConfig(Config cfg)
    {
        if (cfg.FloatingPillLeft is double l && cfg.FloatingPillTop is double t)
        {
            Left = l;
            Top = t;
            return;
        }

        var work = SystemParameters.WorkArea;
        Left = work.Right - 170;
        Top = work.Bottom - 62;
    }

    public void Refresh(ManagerSnapshot s)
    {
        var active = s.ActiveSession is not null;
        OpenEye.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ClosedEye.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        PillHoldToEnd.Visibility = active && !s.PendingEnd ? Visibility.Visible : Visibility.Collapsed;

        if (s.PendingEnd)
        {
            PillText.Text = "Ending…";
            return;
        }

        if (s.ActiveSession is { } sess)
        {
            if (s.Config.ShowFloatingCountdown)
            {
                PillText.Text = SessionMath.CountdownText(
                    sess.EndsAt,
                    sess.CountdownShape,
                    s.Config.CountdownStyle,
                    _clock.Now);
                PillText.Visibility = Visibility.Visible;
            }
            else
            {
                PillText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            PillText.Text = "bekky-wake";
            PillText.Visibility = Visibility.Visible;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && IsInsideHoldButton(fe)) return;
        _dragging = false;
        _mouseDownScreen = PointToScreen(e.GetPosition(this));
        PillBorder.CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!PillBorder.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _mouseDownScreen.X;
        var dy = current.Y - _mouseDownScreen.Y;
        if (!_dragging && Math.Sqrt(dx * dx + dy * dy) > 5)
        {
            _dragging = true;
            PillBorder.ReleaseMouseCapture();
            DragMove();
            _ = _manager.UpdateConfigAsync(c =>
            {
                c.FloatingPillLeft = Left;
                c.FloatingPillTop = Top;
            });
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (PillBorder.IsMouseCaptured) PillBorder.ReleaseMouseCapture();
        if (!_dragging)
        {
            var pt = PointToScreen(e.GetPosition(this));
            _openPopoverAt(new System.Drawing.Point((int)pt.X, (int)pt.Y));
        }
        _dragging = false;
    }

    private static bool IsInsideHoldButton(DependencyObject? obj)
    {
        while (obj is not null)
        {
            if (obj is HoldToEndButton) return true;
            obj = global::System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        return false;
    }
}
