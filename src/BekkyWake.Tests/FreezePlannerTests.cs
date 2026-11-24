using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using Xunit;

namespace BekkyWake.Tests;

public sealed class FreezePlannerTests
{
    [Fact]
    public void PlanFreezeTargets_FreezesDefaultListAndProtectsAgentsAndSystem()
    {
        var cfg = new Config { FreezeAllApps = false };
        RunningAppInfo[] running =
        [
            new(101, "slack", MainWindowTitle: "Slack"),
            new(102, "slack", MainWindowTitle: ""), // child renderer process of Slack
            new(103, "Discord", MainWindowTitle: "Discord"),
            new(104, "Code", MainWindowTitle: "Visual Studio Code"),
            new(105, "explorer", MainWindowTitle: "Program Manager"),
            new(106, "notepad", MainWindowTitle: "notes.txt")
        ];

        var targets = FreezePlanner.PlanFreezeTargets(running, cfg, currentPid: 999);
        Assert.Equal([101, 102, 103], targets.Select(t => t.Pid).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void PlanFreezeTargets_FreezeAllSkipsMeetingAndProtectedApps()
    {
        var cfg = new Config { FreezeAllApps = true };
        RunningAppInfo[] running =
        [
            new(201, "Zoom", MainWindowTitle: "Zoom Meeting"),
            new(202, "obs64", MainWindowTitle: "OBS Studio"),
            new(203, "WindowsTerminal", MainWindowTitle: "PowerShell"),
            new(204, "Spotify", MainWindowTitle: "Spotify Free"),
            new(205, "Spotify", MainWindowTitle: ""), // helper process of windowed Spotify
            new(206, "backgroundDaemon", MainWindowTitle: "") // no window title -> untouched
        ];

        var targets = FreezePlanner.PlanFreezeTargets(running, cfg, currentPid: 999);
        Assert.Equal([204, 205], targets.Select(t => t.Pid).OrderBy(x => x).ToArray());
    }
}
