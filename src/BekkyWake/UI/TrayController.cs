using System.Drawing;
using System.Windows.Threading;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using BekkyWake.Core.Store;
using WinForms = System.Windows.Forms;

namespace BekkyWake.UI;

public sealed class TrayController : INotifier, IDisposable
{
    private readonly SessionManager _manager;
    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly IProcessFreezer _freezer;
    private readonly IBackstopScheduler _backstopScheduler;
    private readonly Dispatcher _dispatcher;

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _contextMenu;
    private readonly PopoverWindow _popover;
    private readonly FloatingPillWindow _floatingPill;
    private SettingsWindow? _settingsWindow;

    private readonly DispatcherTimer _tickTimer;
    private Icon? _currentTrayIcon;
    private bool _lastActive;
    private bool _lastWarningState;
    private double? _lastRingBucket;
    private bool _disposed;

    public TrayController(
        SessionManager manager,
        AppPaths paths,
        IClock clock,
        IProcessFreezer freezer,
        IBackstopScheduler backstopScheduler,
        Dispatcher dispatcher)
    {
        _manager = manager;
        _paths = paths;
        _clock = clock;
        _freezer = freezer;
        _backstopScheduler = backstopScheduler;
        _dispatcher = dispatcher;

        _contextMenu = BuildContextMenu();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "bekky-wake",
            ContextMenuStrip = _contextMenu
        };

        _popover = new PopoverWindow(_manager, _clock, OpenSettings, RequestQuit);
        _floatingPill = new FloatingPillWindow(_manager, _clock, ShowPopoverAt);

        _notifyIcon.MouseClick += OnTrayMouseClick;

        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();

        _manager.StateChanged += OnManagerStateChanged;
        _manager.SessionActivityTransition += OnSessionTransition;

        ApplySnapshot(_manager.CurrentSnapshot);
    }

    public void Post(string title, string body, bool isWarning = false)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _notifyIcon.ShowBalloonTip(
                4500,
                title,
                body,
                isWarning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        });
    }

    public void ShowPopoverNearCursor()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            ShowPopoverAt(WinForms.Control.MousePosition);
        });
    }

    private void ShowPopoverAt(Point screenPt)
    {
        if (_popover.IsVisible)
        {
            _popover.Hide();
            return;
        }
        _popover.ShowNearPoint(screenPt);
    }

    private async void OnTrayMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left) return;

        var s = _manager.CurrentSnapshot;
        var altHeld = (WinForms.Control.ModifierKeys & WinForms.Keys.Alt) != 0;

        if (!altHeld && s.Config.TrayMode == TrayMode.Minimal)
        {
            if (!s.IsActive)
            {
                var (ok, _) = await _manager.TryStartAsync(TimeSpan.FromSeconds(s.Config.DefaultPreset));
                if (!ok) ShowPopoverAt(WinForms.Control.MousePosition);
            }
            else
            {
                await _manager.TryExtendAsync(TimeSpan.FromSeconds(s.Config.DefaultPreset));
            }
            return;
        }

        if (!altHeld && s.Config.OneClickStart && !s.IsActive)
        {
            var (ok, _) = await _manager.TryStartAsync(TimeSpan.FromSeconds(s.Config.DefaultPreset));
            if (!ok) ShowPopoverAt(WinForms.Control.MousePosition);
            return;
        }

        ShowPopoverAt(WinForms.Control.MousePosition);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _manager.TickCountdown();
        var s = _manager.CurrentSnapshot;
        UpdateTooltipAndIcon(s);
        _popover.RefreshCountdownTick();
        if (_floatingPill.IsVisible)
        {
            _floatingPill.Refresh(s);
        }
    }

    private void OnManagerStateChanged(ManagerSnapshot snapshot)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            ApplySnapshot(snapshot);
        });
    }

    private void OnSessionTransition(bool started, EndReason? reason)
    {
        var s = _manager.CurrentSnapshot;
        if (!s.Config.SessionSounds || s.IsLidClosed) return;

        if (started)
        {
            SessionSounds.PlayStart();
        }
        else if (reason == EndReason.User)
        {
            SessionSounds.PlayEnd();
        }
    }

    private void ApplySnapshot(ManagerSnapshot s)
    {
        UpdateTooltipAndIcon(s);
        _popover.UpdateSnapshot(s);

        if (s.Config.TrayMode == TrayMode.FloatingPill)
        {
            if (!_floatingPill.IsVisible)
            {
                _floatingPill.ApplyPositionFromConfig(s.Config);
                _floatingPill.Show();
            }
            _floatingPill.Refresh(s);
        }
        else if (_floatingPill.IsVisible)
        {
            _floatingPill.Hide();
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.LoadFromSnapshot(s);
        }

        RefreshContextMenu(s);
    }

    private void UpdateTooltipAndIcon(ManagerSnapshot s)
    {
        var active = s.IsActive;
        var hasWarn = s.Warnings.Count > 0 || s.IsJournalUnreadable || s.WaitingAudioOutputs.Count > 0;
        double? ringProgress = null;

        if (active && s.ActiveSession is { } sess)
        {
            var now = _clock.Now;
            var rem = sess.RemainingSeconds(now);
            var totalSpan = Math.Max((sess.EndsAt - sess.StartedAt).TotalSeconds, 1);
            if (s.Config.TrayMode == TrayMode.Minimal)
            {
                // Bucket to 2% increments so we don't regenerate HICON every second unnecessarily.
                var rawFrac = Math.Clamp(rem / totalSpan, 0.0, 1.0);
                ringProgress = Math.Ceiling(rawFrac * 50.0) / 50.0;
            }

            var text = $"bekky-wake · {SessionMath.FormatRemaining(rem)} left";
            _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
        }
        else
        {
            _notifyIcon.Text = hasWarn ? "bekky-wake · Needs attention" : "bekky-wake · Inactive";
        }

        if (_currentTrayIcon is null
            || active != _lastActive
            || hasWarn != _lastWarningState
            || ringProgress != _lastRingBucket)
        {
            _lastActive = active;
            _lastWarningState = hasWarn;
            _lastRingBucket = ringProgress;

            var nextIcon = EyeIconRenderer.CreateTrayIcon(active, hasWarn, ringProgress);
            var old = _currentTrayIcon;
            _currentTrayIcon = nextIcon;
            _notifyIcon.Icon = nextIcon;
            old?.Dispose();
        }
    }

    private WinForms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false
        };
        return menu;
    }

    private void RefreshContextMenu(ManagerSnapshot s)
    {
        _contextMenu.Items.Clear();

        var statusLabel = s.IsActive && s.ActiveSession is { } sess
            ? $"Awake · {SessionMath.FormatRemaining(sess.RemainingSeconds(_clock.Now))} left"
            : "Inactive · Lid sleep normal";
        var statusItem = new WinForms.ToolStripMenuItem(statusLabel) { Enabled = false };
        _contextMenu.Items.Add(statusItem);
        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());

        var openItem = new WinForms.ToolStripMenuItem("Open Controls…");
        openItem.Click += (_, _) => ShowPopoverAt(WinForms.Control.MousePosition);
        _contextMenu.Items.Add(openItem);

        if (s.IsActive)
        {
            var endItem = new WinForms.ToolStripMenuItem("End Session");
            endItem.Click += async (_, _) => await _manager.EndAsync(EndReason.User);
            _contextMenu.Items.Add(endItem);
        }
        else
        {
            var startDefaultItem = new WinForms.ToolStripMenuItem($"Start {SessionMath.FormatPresetLabel(s.Config.DefaultPreset)}");
            startDefaultItem.Click += async (_, _) => await _manager.TryStartAsync(TimeSpan.FromSeconds(s.Config.DefaultPreset));
            _contextMenu.Items.Add(startDefaultItem);
        }

        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());

        var settingsItem = new WinForms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        _contextMenu.Items.Add(settingsItem);

        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => RequestQuit();
        _contextMenu.Items.Add(quitItem);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_manager, _paths, _freezer, _backstopScheduler);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private async void RequestQuit()
    {
        if (_manager.CurrentSnapshot.IsActive)
        {
            await _manager.EndAsync(EndReason.Quit);
        }

        if (!_manager.CanQuit(out var reason))
        {
            Post("bekky-wake cannot quit yet", reason, isWarning: true);
            ShowPopoverAt(WinForms.Control.MousePosition);
            return;
        }

        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Stop();
        _manager.StateChanged -= OnManagerStateChanged;
        _manager.SessionActivityTransition -= OnSessionTransition;
        _popover.Close();
        _floatingPill.Close();
        _settingsWindow?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentTrayIcon?.Dispose();
        _contextMenu.Dispose();
    }

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? Color.FromArgb(0xE6, 0xE3, 0xDD)
                : Color.FromArgb(0x9C, 0xA3, 0xAF);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x22, 0x25, 0x29);
        private static readonly Color Hover = Color.FromArgb(0x3A, 0x3F, 0x46);
        private static readonly Color Border = Color.FromArgb(0x38, 0x3D, 0x43);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
