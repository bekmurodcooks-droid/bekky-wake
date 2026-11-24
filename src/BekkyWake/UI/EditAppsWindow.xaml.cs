using System.Windows;
using System.Windows.Controls;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.UI;

public partial class EditAppsWindow : Window
{
    private readonly SessionManager _manager;
    private readonly IProcessFreezer _freezer;
    private bool _initializing;

    public EditAppsWindow(SessionManager manager, IProcessFreezer freezer)
    {
        InitializeComponent();
        _manager = manager;
        _freezer = freezer;
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            EyeIconRenderer.CreateAppWindowIcon().Handle,
            Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        Loaded += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        _initializing = true;
        var s = _manager.CurrentSnapshot;
        FreezeAllCheck.IsChecked = s.Config.FreezeAllApps;
        _initializing = false;

        var running = _freezer.ListRunningApps();
        var listed = s.Config.ListedApps;
        var runningNames = running
            .Where(r => !string.IsNullOrWhiteSpace(r.MainWindowTitle))
            .Select(r => FreezePlanner.NormalizeName(r.ProcessName))
            .Where(n => !FreezePlanner.IsHardDenied(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var allNames = listed
            .Concat(runningNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => s.Config.RuleFor(n) == AppRule.Freeze ? 0 : s.Config.RuleFor(n) == AppRule.KeepRunning ? 1 : 2)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppsListPanel.Children.Clear();
        var chipStyle = (Style)FindResource("ChipButtonStyle");
        var promStyle = (Style)FindResource("ProminentButtonStyle");

        foreach (var name in allNames)
        {
            var rule = s.Config.RuleFor(name);
            var protectedByDefault = FreezePlanner.IsBuiltInProtected(name);
            var hardDenied = FreezePlanner.IsHardDenied(name);

            var row = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelText = protectedByDefault && rule is null ? $"{name} (protected)" : name;
            var label = new TextBlock
            {
                Text = labelText,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushText"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var keepBtn = new System.Windows.Controls.Button
            {
                Content = "Keep running",
                Style = rule == AppRule.KeepRunning ? promStyle : chipStyle,
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 11
            };
            keepBtn.Click += async (_, _) =>
            {
                await _manager.UpdateConfigAsync(c => c.SetRule(AppRule.KeepRunning, name));
                Rebuild();
            };
            Grid.SetColumn(keepBtn, 1);

            var freezeBtn = new System.Windows.Controls.Button
            {
                Content = "Freeze",
                Style = rule == AppRule.Freeze ? promStyle : chipStyle,
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 11,
                IsEnabled = !hardDenied
            };
            freezeBtn.Click += async (_, _) =>
            {
                await _manager.UpdateConfigAsync(c => c.SetRule(AppRule.Freeze, name));
                Rebuild();
            };
            Grid.SetColumn(freezeBtn, 3);

            var removeBtn = new System.Windows.Controls.Button
            {
                Content = "×",
                Style = chipStyle,
                Padding = new Thickness(7, 4, 7, 4),
                FontSize = 11,
                IsEnabled = rule is not null,
                ToolTip = "Remove explicit rule"
            };
            removeBtn.Click += async (_, _) =>
            {
                await _manager.UpdateConfigAsync(c => c.SetRule(null, name));
                Rebuild();
            };
            Grid.SetColumn(removeBtn, 5);

            row.Children.Add(label);
            row.Children.Add(keepBtn);
            row.Children.Add(freezeBtn);
            row.Children.Add(removeBtn);
            AppsListPanel.Children.Add(row);
        }

        var targets = FreezePlanner.PlanFreezeTargets(running, s.Config)
            .Select(t => FreezePlanner.NormalizeName(t.ProcessName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PreviewText.Text = targets.Count == 0
            ? "Would freeze now (if lid closes): nothing running matches freeze rules."
            : $"Would freeze now (if lid closes): {string.Join(", ", targets)}";
    }

    private async void FreezeAllChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var val = FreezeAllCheck.IsChecked == true;
        await _manager.UpdateConfigAsync(c => c.FreezeAllApps = val);
        Rebuild();
    }

    private async void AddKeepRunningClicked(object sender, RoutedEventArgs e) => await AddManualRuleAsync(AppRule.KeepRunning);

    private async void AddFreezeClicked(object sender, RoutedEventArgs e) => await AddManualRuleAsync(AppRule.Freeze);

    private async Task AddManualRuleAsync(AppRule rule)
    {
        var norm = FreezePlanner.NormalizeName(AddProcessBox.Text);
        if (string.IsNullOrEmpty(norm)) return;
        AddProcessBox.Text = string.Empty;
        await _manager.UpdateConfigAsync(c => c.SetRule(rule, norm));
        Rebuild();
    }

    private void RefreshClicked(object sender, RoutedEventArgs e) => Rebuild();

    private void DoneClicked(object sender, RoutedEventArgs e) => Close();
}
