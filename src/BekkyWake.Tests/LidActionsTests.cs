using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using Xunit;

namespace BekkyWake.Tests;

public sealed class LidActionsTests
{
    [Fact]
    public async Task ApplyAndUndo_JournalsBeforeMutatingAndRestoresCleanly()
    {
        using var scope = new TempHomeScope();
        var display = new FakeDisplayController { Brightness = 75 };
        var audio = new FakeAudioController();
        audio.AddConnected("dev-1", "Speakers", 0.6f, muted: false);
        audio.AddConnected("dev-2", "Headphones", 0.3f, muted: true);

        var freezer = new FakeProcessFreezer();
        freezer.Running.Add(new RunningAppInfo(401, "slack", MainWindowTitle: "Slack"));
        freezer.CreationTicks[401] = 8888;
        freezer.Running.Add(new RunningAppInfo(402, "Code", MainWindowTitle: "VS Code"));
        freezer.CreationTicks[402] = 9999;
        freezer.Running.Add(new RunningAppInfo(501, "Docker Desktop"));
        freezer.CreationTicks[501] = 5555;

        var docker = new FakeDockerController { DockerApp = new RunningAppInfo(501, "Docker Desktop"), NextStatus = DockerCheckStatus.Idle };
        var locker = new FakeScreenLocker();
        var actions = new LidActions(scope.Store, display, audio, freezer, docker, locker, scope.Log);

        var cfg = new Config { DockerRule = true, LockScreenOnLidClose = true };
        var state = new RuntimeState();

        var closeRes = await actions.ApplyLidCloseAsync(cfg, state);
        Assert.Empty(closeRes.Warnings);
        Assert.Equal(1, locker.LockCount);
        Assert.True(display.DisplayOff);
        Assert.Equal((byte)0, display.Brightness);
        Assert.Contains("dev-1", audio.MutedIds);
        Assert.Contains(401, freezer.SuspendedPids);
        Assert.DoesNotContain(402, freezer.SuspendedPids);
        Assert.Contains(501, freezer.SuspendedPids);
        Assert.True(state.DockerFrozen);

        // Simulate Headphones disconnecting before lid open:
        audio.ConnectedIds.Remove("dev-2");

        var openRes = actions.UndoLidClose(state);
        Assert.Empty(openRes.Warnings);
        Assert.False(display.DisplayOff);
        Assert.Equal((byte)75, display.Brightness);
        Assert.DoesNotContain("dev-1", audio.MutedIds);
        Assert.DoesNotContain(401, freezer.SuspendedPids);
        Assert.DoesNotContain(501, freezer.SuspendedPids);
        Assert.False(state.DockerFrozen);
        // Disconnected Headphones stay saved waiting to reconnect!
        var waiting = Assert.Single(state.SavedAudioOutputs);
        Assert.Equal("dev-2", waiting.DeviceId);
    }

    [Fact]
    public void UndoLidClose_NeverAutoResumesProvisionalUnconfirmedProcesses()
    {
        using var scope = new TempHomeScope();
        var display = new FakeDisplayController();
        var audio = new FakeAudioController();
        var freezer = new FakeProcessFreezer();
        freezer.Running.Add(new RunningAppInfo(777, "Discord", MainWindowTitle: "Discord"));
        freezer.CreationTicks[777] = 12345;
        freezer.SuspendedPids.Add(777);

        var state = new RuntimeState();
        state.FrozenProcesses.Add(new FrozenProcess(777, "Discord", Identity: null));

        var actions = new LidActions(scope.Store, display, audio, freezer, new FakeDockerController(), new FakeScreenLocker(), scope.Log);
        var res = actions.UndoLidClose(state);

        Assert.Single(res.Warnings);
        Assert.Contains(777, freezer.SuspendedPids); // NOT resumed without verified kernel identity
        Assert.Single(state.FrozenProcesses);
    }
}
