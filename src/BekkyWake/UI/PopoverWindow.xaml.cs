using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.UI;

public partial class PopoverWindow : Window
{
    private readonly SessionManager _manager;
    private readonly IClock _clock;
    private readonly Action _openSettings;
    private readonly Action _requestQuit;

    private ManagerSnapshot _snapshot;
    private bool _customShown;
    private bool _suppressDeactivate;

    public PopoverWindow(
        SessionManager manager,
        IClock clock,
        Action openSettings,
        Action requestQuit)
    {
        InitializeComponent();
        _manager = manager;
        _clock = clock;
        _openSettings = openSettings;
        _requestQuit = requestQuit;
        _snapshot = manager.CurrentSnapshot;

        HoldToEndBtn.HoldCompleted += async () => await _manager.EndAsync(EndReason.User);

        Deactivated += (_, _) =>
        {
            if (!_suppressDeactivate && IsVisible)
            {
                Hide();
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
    }

    public void ShowNearPoint(System.Drawing.Point physicalPoint)
    {
        _snapshot = _manager.CurrentSnapshot;
        RefreshView(_snapshot);

        // Force measure before positioning.
        UpdateLayout();
        Show();
        UpdateLayout();

        var screen = global::System.Windows.Forms.Screen.FromPoint(physicalPoint);
        var work = screen.WorkingArea;

        var source = PresentationSource.FromVisual(this);
        var dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var workLeft = work.Left / dpiScaleX;
        var workTop = work.Top / dpiScaleY;
        var workRight = work.Right / dpiScaleX;
        var workBottom = work.Bottom / dpiScaleY;

        var clickX = physicalPoint.X / dpiScaleX;
        var clickY = physicalPoint.Y / dpiScaleY;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 340;

        var left = Math.Clamp(clickX - width / 2.0, workLeft + 8, workRight - width - 8);
        var top = clickY - height - 10;
        if (top < workTop + 8)
        {
            top = Math.Min(clickY + 12, workBottom - height - 8);
        }

        Left = left;
        Top = top;
        Activate();
    }

    public void UpdateSnapshot(ManagerSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (IsVisible)
        {
            RefreshView(snapshot);
        }
    }

    public void RefreshCountdownTick()
    {
        if (!IsVisible) return;
        UpdateHeaderOnly(_manager.CurrentSnapshot);
    }

    private void UpdateHeaderOnly(ManagerSnapshot s)
    {
        var now = _clock.Now;
        if (s.PendingEnd)
        {
            HeaderTitle.Text = "Ending…";
            HeaderSubtitle.Text = "Restoring system power settings";
            return;
        }

        if (s.ActiveSession is { } sess)
        {
            var remaining = SessionMath.CountdownText(
                sess.EndsAt,
                sess.CountdownShape,
                s.Config.CountdownStyle,
                now);
            HeaderTitle.Text = s.Config.CountdownStyle == CountdownStyle.Remaining
                ? $"Awake · {remaining}"
                : $"Awake · Until {remaining}";
            HeaderSubtitle.Text = s.Config.CountdownStyle == CountdownStyle.Remaining
                ? $"Ends at {SessionMath.FormatEndTime(sess.EndsAt, now)}"
                : $"{SessionMath.FormatRemaining(sess.RemainingSeconds(now))} left";
        }
        else
        {
            HeaderTitle.Text = "bekky-wake";
            HeaderSubtitle.Text = "Not active · Lid sleep normal";
        }
    }

    private void RefreshView(ManagerSnapshot s)
    {
        var active = s.ActiveSession is not null;

        OpenEyeGroup.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ClosedEyeGroup.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        HoldToEndBtn.Visibility = active && !s.PendingEnd ? Visibility.Visible : Visibility.Collapsed;
        SimBadge.Visibility = s.IsSimulationBuild ? Visibility.Visible : Visibility.Collapsed;

        UpdateHeaderOnly(s);

        ChipsHeading.Text = active ? "ADD TIME" : "STAY AWAKE FOR";
        CustomCommitBtn.Content = active ? "Extend" : "Start";

        BuildDurationChips(s, active);
        CustomDurationPanel.Visibility = _customShown ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(s.LastError))
        {
            ErrorText.Text = s.LastError;
            ErrorBanner.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        BuildStatusBadges(s);
        BuildAlerts(s);
    }

    private void BuildDurationChips(ManagerSnapshot s, bool active)
    {
        ChipsPanel.Children.Clear();
        var chipStyle = (Style)FindResource("ChipButtonStyle");

        if (!active)
        {
            foreach (var sec in s.Config.Presets)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = SessionMath.FormatPresetLabel(sec),
                    Style = chipStyle,
                    Margin = new Thickness(0, 0, 6, 6),
                    MinWidth = 48
                };
                btn.Click += async (_, _) =>
                {
                    _customShown = false;
                    await _manager.TryStartAsync(TimeSpan.FromSeconds(sec));
                };
                ChipsPanel.Children.Add(btn);
            }
        }
        else
        {
            double[] extendSecs = [30 * 60, 3600, 2 * 3600];
            foreach (var sec in extendSecs)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = "+" + SessionMath.FormatPresetLabel(sec),
                    Style = chipStyle,
                    Margin = new Thickness(0, 0, 6, 6),
                    MinWidth = 54
                };
                btn.Click += async (_, _) =>
                {
                    _customShown = false;
                    await _manager.TryExtendAsync(TimeSpan.FromSeconds(sec));
                };
                ChipsPanel.Children.Add(btn);
            }
        }

        var customBtn = new System.Windows.Controls.Button
        {
            Content = "Custom",
            Style = chipStyle,
            Margin = new Thickness(0, 0, 6, 6)
        };
        customBtn.Click += (_, _) =>
        {
            _customShown = !_customShown;
            CustomDurationPanel.Visibility = _customShown ? Visibility.Visible : Visibility.Collapsed;
            if (_customShown) FocusCustomField();
        };
        ChipsPanel.Children.Add(customBtn);
    }

    private void FocusCustomField()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var target = _snapshot.Config.DefaultFocusField switch
            {
                DurationField.Days => DaysInput,
                DurationField.Minutes => MinutesInput,
                _ => HoursInput
            };
            target.Focus();
            target.SelectAll();
        });
    }

    private void BuildStatusBadges(ManagerSnapshot s)
    {
        StatusBadgesPanel.Children.Clear();

        var battLabel = s.Power.Battery switch
        {
            BatteryStatus.Reading r => s.Power.IsCharging ? $"Battery {r.Percent}% (Charging)" : $"Battery {r.Percent}%",
            BatteryStatus.Unreadable => "Battery unreadable",
            _ => "Desktop AC"
        };
        AddBadge(battLabel, null);

        var thermLabel = s.Power.Thermal switch
        {
            ThermalLevel.Unavailable => "Temp N/A",
            _ => s.Power.TemperatureCelsius is double c
                ? $"{s.Power.Thermal} ({c:0.#}°C)"
                : s.Power.Thermal.ToString()
        };
        var thermColor = s.Power.Thermal is ThermalLevel.Serious or ThermalLevel.Critical
            ? (System.Windows.Media.Brush)FindResource("BrushWarning")
            : null;
        AddBadge(thermLabel, thermColor);

        var lidLabel = s.IsLidClosed
            ? (s.HasExternalDisplay ? "Lid closed (External display)" : "Lid closed")
            : "Lid open";
        AddBadge(lidLabel, null);

        if (s.Config.KeepScreenAwake)
        {
            AddBadge("Screen stays unlocked", (System.Windows.Media.Brush)FindResource("BrushAccent"));
        }
    }

    private void AddBadge(string text, System.Windows.Media.Brush? foregroundOverride)
    {
        var border = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("BrushSurface"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 5)
        };
        border.Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = foregroundOverride ?? (System.Windows.Media.Brush)FindResource("BrushMuted")
        };
        StatusBadgesPanel.Children.Add(border);
    }

    private void BuildAlerts(ManagerSnapshot s)
    {
        AlertsPanel.Children.Clear();
        var chipStyle = (Style)FindResource("ChipButtonStyle");

        foreach (var warn in s.Warnings)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 245, 158, 11)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 245, 158, 11)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };
            row.Child = new TextBlock
            {
                Text = warn,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap
            };
            AlertsPanel.Children.Add(row);
        }

        foreach (var audio in s.WaitingAudioOutputs)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = $"Stop waiting for {audio.FriendlyName ?? "audio output"}",
                Style = chipStyle,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 6)
            };
            btn.Click += async (_, _) => await _manager.StopWaitingForAudioAsync(audio.DeviceId);
            AlertsPanel.Children.Add(btn);
        }

        foreach (var browser in s.ThrottledBrowsers)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = $"Relaunch {browser.DisplayName} unthrottled",
                Style = chipStyle,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 6),
                ToolTip = $"Missing flags: {string.Join(", ", browser.MissingFlags)}"
            };
            btn.Click += async (_, _) =>
            {
                _suppressDeactivate = true;
                try
                {
                    var answer = System.Windows.MessageBox.Show(
                        $"Quit and relaunch {browser.DisplayName} with background-throttling disabled?\n\nOpen tabs will only restore if {browser.DisplayName} is configured to reopen tabs on startup.",
                        "Relaunch Unthrottled",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                    {
                        await _manager.RelaunchBrowserAsync(browser);
                    }
                }
                finally
                {
                    _suppressDeactivate = false;
                }
            };
            AlertsPanel.Children.Add(btn);
        }
    }

    private void NumericOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private async void CustomFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitCustomAsync();
        }
    }

    private async void CustomCommitClicked(object sender, RoutedEventArgs e) => await CommitCustomAsync();

    private async Task CommitCustomAsync()
    {
        _ = int.TryParse(DaysInput.Text, out var days);
        _ = int.TryParse(HoursInput.Text, out var hours);
        _ = int.TryParse(MinutesInput.Text, out var mins);

        var totalSeconds = days * 86400.0 + hours * 3600.0 + mins * 60.0;
        if (totalSeconds < SessionMath.MinimumDurationSeconds)
        {
            ErrorText.Text = "Minimum duration is 1 minute.";
            ErrorBanner.Visibility = Visibility.Visible;
            return;
        }

        _customShown = false;
        if (_snapshot.IsActive)
        {
            await _manager.TryExtendAsync(TimeSpan.FromSeconds(totalSeconds));
        }
        else
        {
            await _manager.TryStartAsync(TimeSpan.FromSeconds(totalSeconds));
        }
    }

    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettings();
    }

    private void QuitClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _requestQuit();
    }
}
