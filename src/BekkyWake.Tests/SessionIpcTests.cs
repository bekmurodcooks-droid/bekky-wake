using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using Xunit;

namespace BekkyWake.Tests;

public sealed class SessionIpcTests
{
    [Fact]
    public async Task Cli_UsageErrorsReturnExit64AndStatusReadsDisk()
    {
        using var scope = new TempHomeScope();
        var clock = new FakeClock();

        var bad = await SessionCli.ExecuteAsync(["invalid"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitUsage, bad.ExitCode);

        var idleStatus = await SessionCli.ExecuteAsync(["status"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitOk, idleStatus.ExitCode);
        Assert.Equal("no session is running", idleStatus.Line);

        scope.Store.SaveSession(SessionMath.NewSession(clock.Now, 3600, 86400));
        var activeStatus = await SessionCli.ExecuteAsync(["status"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitOk, activeStatus.ExitCode);
        Assert.StartsWith("running until ", activeStatus.Line);
    }

    [Fact]
    public async Task NamedPipeIpc_RoundTripsStartExtendStatusAndEnd()
    {
        using var scope = new TempHomeScope();
        var clock = new FakeClock();
        var guard = new FakeSleepGuard();
        var power = new FakePowerMonitor();
        var lid = new FakeLidObserver();
        var display = new FakeDisplayController();
        var audio = new FakeAudioController();
        var freezer = new FakeProcessFreezer();
        var docker = new FakeDockerController();
        var backstop = new FakeBackstopScheduler();
        var notifier = new FakeNotifier();
        var browsers = new FakeBrowserController();
        var locker = new FakeScreenLocker();
        var startup = new FakeStartupManager();
        var lidActions = new LidActions(scope.Store, display, audio, freezer, docker, locker, scope.Log);

        using var mgr = new SessionManager(
            scope.Paths, scope.Store, scope.RecoveryLock, clock, guard, power, lid, audio,
            backstop, notifier, browsers, startup, lidActions, scope.Log, @"C:\Test\bekky-wake.exe");
        await mgr.ReconcileAsync();

        using var server = new SessionIpcServer(scope.Paths, mgr, clock, scope.Log);
        server.Start();

        var startRes = await SessionCli.ExecuteAsync(["start", "3600"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitOk, startRes.ExitCode);
        Assert.Contains("running until", startRes.Line);
        Assert.True(mgr.CurrentSnapshot.IsActive);

        var extRes = await SessionCli.ExecuteAsync(["extend", "1800"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitOk, extRes.ExitCode);
        Assert.Equal(clock.Now.AddMinutes(90), mgr.CurrentSnapshot.ActiveSession!.EndsAt);

        var endRes = await SessionCli.ExecuteAsync(["end"], scope.Paths, scope.Store, clock);
        Assert.Equal(SessionCli.ExitOk, endRes.ExitCode);
        Assert.Equal("no session is running", endRes.Line);
        Assert.False(mgr.CurrentSnapshot.IsActive);
    }
}
