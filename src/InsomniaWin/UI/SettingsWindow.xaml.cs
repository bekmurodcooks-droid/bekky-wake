using System.Diagnostics;
using System.Windows;
using InsomniaWin.Core.Core;
using InsomniaWin.Core.Models;
using InsomniaWin.Core.Store;

namespace InsomniaWin.UI;

public partial class SettingsWindow : Window
{
    private readonly SessionManager _manager;
    private readonly AppPaths _paths;
    private readonly IProcessFreezer _freezer;
    private readonly IBackstopScheduler _backstopScheduler;
    private bool _loading;

    public SettingsWindow(
        SessionManager manager,
        AppPaths paths,
        IProcessFreezer freezer,
        IBackstopScheduler backstopScheduler)
    {
        InitializeComponent();
        _manager = manager;
        _paths = paths;
        _freezer = freezer;
        _backstopScheduler = backstopScheduler;

        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            EyeIconRenderer.CreateAppWindowIcon().Handle,
            Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        PopulateStaticChoices();
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private void PopulateStaticChoices()
    {
        TrayModeBox.ItemsSource = new[]
        {
            new ComboItem<TrayMode>(TrayMode.Popover, "Tray Eye + Popover (Default)"),
            new ComboItem<TrayMode>(TrayMode.FloatingPill, "Floating Pill + Tray Eye"),
            new ComboItem<TrayMode>(TrayMode.Minimal, "Minimal Eye Ring (Click adds time)")
        };
        TrayModeBox.DisplayMemberPath = "Label";

        CountdownStyleBox.ItemsSource = new[]
        {
            new ComboItem<CountdownStyle>(CountdownStyle.Remaining, "Time remaining"),
            new ComboItem<CountdownStyle>(CountdownStyle.EndTime, "End clock time")
        };
        CountdownStyleBox.DisplayMemberPath = "Label";
    }

    public void LoadFromSnapshot(ManagerSnapshot s)
    {
        _loading = true;
        try
        {
            var cfg = s.Config;

            TrayModeBox.SelectedItem = ((ComboItem<TrayMode>[])TrayModeBox.ItemsSource)
                .FirstOrDefault(x => x.Value == cfg.TrayMode);

            var presetItems = cfg.Presets
                .Select(sec => new ComboItem<double>(sec, SessionMath.FormatPresetLabel(sec)))
                .ToArray();
            DefaultPresetBox.ItemsSource = presetItems;
            DefaultPresetBox.DisplayMemberPath = "Label";
            DefaultPresetBox.SelectedItem = presetItems.FirstOrDefault(x => Math.Abs(x.Value - cfg.DefaultPreset) < 1)
                ?? presetItems.FirstOrDefault();

            CountdownStyleBox.SelectedItem = ((ComboItem<CountdownStyle>[])CountdownStyleBox.ItemsSource)
                .FirstOrDefault(x => x.Value == cfg.CountdownStyle);

            OneClickCheck.IsChecked = cfg.OneClickStart;
            OneClickCheck.IsEnabled = cfg.TrayMode != TrayMode.Minimal;
            ShowFloatingCountdownCheck.IsChecked = cfg.ShowFloatingCountdown;
            KeepScreenAwakeCheck.IsChecked = cfg.KeepScreenAwake;
            SoundsCheck.IsChecked = cfg.SessionSounds;
            LaunchAtLoginCheck.IsChecked = cfg.LaunchAtLogin;

            MuteLidCheck.IsChecked = cfg.MuteOnLidClose;
            DarkenDisplayCheck.IsChecked = cfg.DarkenDisplayOnLidClose;
            LowPowerLidCheck.IsChecked = cfg.LowPowerOnLidClose;
            LockScreenCheck.IsChecked = cfg.LockScreenOnLidClose;
            DockerRuleCheck.IsChecked = cfg.DockerRule;

            var freezeCount = cfg.AppsWithRule(AppRule.Freeze).Count;
            var agentCount = cfg.AppsWithRule(AppRule.KeepRunning).Count;
            var allOther = cfg.FreezeAllApps ? "All other desktop apps freeze" : "All other apps keep running";
            AppsSummaryText.Text = $"{freezeCount} set to freeze · {agentCount} kept running · {allOther}";

            var lowChoices = Config.FloorChoices(100, cfg.LowPowerFloor)
                .Select(v => new ComboItem<int>(v, v == 0 ? "Never" : $"{v}%"))
                .ToArray();
            LowPowerFloorBox.ItemsSource = lowChoices;
            LowPowerFloorBox.DisplayMemberPath = "Label";
            LowPowerFloorBox.SelectedItem = lowChoices.FirstOrDefault(x => x.Value == cfg.LowPowerFloor);

            var endChoices = Config.FloorChoices(Config.MaxEndFloor, cfg.EndFloor)
                .Select(v => new ComboItem<int>(v, v == 0 ? "Never" : $"{v}%"))
                .ToArray();
            EndFloorBox.ItemsSource = endChoices;
            EndFloorBox.DisplayMemberPath = "Label";
            EndFloorBox.SelectedItem = endChoices.FirstOrDefault(x => x.Value == cfg.EndFloor);

            ThermalRulesCheck.IsChecked = cfg.ThermalRules;
            ThermalHoldCheck.IsChecked = cfg.ThermalHoldUntilNominal;

            var battText = s.Power.Battery switch
            {
                BatteryStatus.Reading r => $"Battery: {r.Percent}% ({(s.Power.IsCharging ? "AC / Charging" : "On Battery")})",
                BatteryStatus.Unreadable => "Battery: Unreadable",
                _ => "Battery: Desktop (No battery)"
            };
            var thermText = s.Power.Thermal == ThermalLevel.Unavailable
                ? "Thermal sensors: Not exposed by this laptop's firmware"
                : $"Thermal: {s.Power.Thermal} ({s.Power.TemperatureCelsius:0.#}°C)";
            HardwareReadoutText.Text = $"{battText}  ·  {thermText}";

            var taskOk = _backstopScheduler.IsRegistered();
            BackstopStatusText.Text = taskOk
                ? "Task Scheduler Backstop: Registered (runs every 1 minute)"
                : "Task Scheduler Backstop: Not registered yet (auto-registers on first session)";
            DataDirText.Text = $"Data & Logs: {_paths.RootDir}";
        }
        finally
        {
            _loading = false;
        }
    }

    private async void CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await _manager.UpdateConfigAsync(c =>
        {
            c.OneClickStart = OneClickCheck.IsChecked == true;
            c.ShowFloatingCountdown = ShowFloatingCountdownCheck.IsChecked == true;
            c.KeepScreenAwake = KeepScreenAwakeCheck.IsChecked == true;
            c.SessionSounds = SoundsCheck.IsChecked == true;
            c.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
            c.MuteOnLidClose = MuteLidCheck.IsChecked == true;
            c.DarkenDisplayOnLidClose = DarkenDisplayCheck.IsChecked == true;
            c.LowPowerOnLidClose = LowPowerLidCheck.IsChecked == true;
            c.LockScreenOnLidClose = LockScreenCheck.IsChecked == true;
            c.DockerRule = DockerRuleCheck.IsChecked == true;
            c.ThermalRules = ThermalRulesCheck.IsChecked == true;
            c.ThermalHoldUntilNominal = ThermalHoldCheck.IsChecked == true;
        });
    }

    private async void TrayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || TrayModeBox.SelectedItem is not ComboItem<TrayMode> item) return;
        await _manager.UpdateConfigAsync(c => c.TrayMode = item.Value);
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private async void DefaultPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || DefaultPresetBox.SelectedItem is not ComboItem<double> item) return;
        await _manager.UpdateConfigAsync(c => c.DefaultPreset = item.Value);
    }

    private async void CountdownStyleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || CountdownStyleBox.SelectedItem is not ComboItem<CountdownStyle> item) return;
        await _manager.UpdateConfigAsync(c => c.CountdownStyle = item.Value);
    }

    private async void LowPowerFloorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LowPowerFloorBox.SelectedItem is not ComboItem<int> item) return;
        await _manager.UpdateConfigAsync(c => c.SetLowPowerFloor(item.Value));
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private async void EndFloorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || EndFloorBox.SelectedItem is not ComboItem<int> item) return;
        await _manager.UpdateConfigAsync(c => c.SetEndFloor(item.Value));
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private void EditAppsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new EditAppsWindow(_manager, _freezer) { Owner = this };
        dlg.ShowDialog();
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private void OpenLogsClicked(object sender, RoutedEventArgs e)
    {
        _paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.LogsDir) { UseShellExecute = true });
    }

    private void RegisterBackstopClicked(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrEmpty(exe))
        {
            _backstopScheduler.EnsureRegistered(exe);
        }
        LoadFromSnapshot(_manager.CurrentSnapshot);
    }

    private sealed record ComboItem<T>(T Value, string Label);
}
