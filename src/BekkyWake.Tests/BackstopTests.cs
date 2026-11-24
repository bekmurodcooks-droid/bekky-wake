using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using Xunit;

namespace BekkyWake.Tests;

public sealed class BackstopTests
{
    [Fact]
    public void Backstop_LeavesValidUnexpiredSessionUntouched()
    {
        using var scope = new TempHomeScope();
        var clock = new FakeClock();
        var guard = new FakeSleepGuard();
        var freezer = new FakeProcessFreezer();

        scope.Store.SaveSession(SessionMath.NewSession(clock.Now, 3600, 86400));
        scope.Store.SaveState(new RuntimeState
        {
            SleepDisabledByUs = true,
            SavedLidAction = new SavedLidAction(guard.CurrentLidAction.SchemeGuid, 1, 1)
        });
        guard.CurrentLidAction = guard.CurrentLidAction with { OriginalAcIndex = 0, OriginalDcIndex = 0 };

        var runner = new BackstopRunner(scope.Store, scope.RecoveryLock, clock, guard, freezer, scope.Log);
        var status = runner.Run();

        Assert.Equal(BackstopStatus.CleanOrActive, status);
        Assert.True(File.Exists(scope.Paths.SessionFile));
        Assert.Equal(0u, guard.CurrentLidAction.OriginalAcIndex);
    }

    [Fact]
    public void Backstop_RecoversExpiredSessionAndPreservesAppOnlyAudioEntries()
    {
        using var scope = new TempHomeScope();
        var clock = new FakeClock();
        var guard = new FakeSleepGuard();
        var freezer = new FakeProcessFreezer();

        freezer.Running.Add(new RunningAppInfo(555, "slack"));
        freezer.CreationTicks[555] = 77777;
        freezer.SuspendedPids.Add(555);

        scope.Store.SaveSession(new Session(clock.Now.AddHours(-2), clock.Now.AddMinutes(-5)));
        scope.Store.SaveState(new RuntimeState
        {
            SleepDisabledByUs = true,
            SavedLidAction = new SavedLidAction(guard.CurrentLidAction.SchemeGuid, 1, 2),
            LowPowerSetByUs = true,
            SavedPowerMode = new SavedPowerMode(guard.CurrentLidAction.SchemeGuid, 20, null),
            FrozenProcesses = [new FrozenProcess(555, "slack", new ProcessIdentity(77777, "boot-1"))],
            SavedAudioOutputs = [new SavedAudioOutput("dev-1", "Speakers", 0.5f, false, "save-1")]
        });
        guard.CurrentLidAction = guard.CurrentLidAction with { OriginalAcIndex = 0, OriginalDcIndex = 0 };

        var runner = new BackstopRunner(scope.Store, scope.RecoveryLock, clock, guard, freezer, scope.Log);
        var status = runner.Run();

        Assert.Equal(BackstopStatus.Recovered, status);
        Assert.Equal(1u, guard.CurrentLidAction.OriginalAcIndex);
        Assert.Equal(2u, guard.CurrentLidAction.OriginalDcIndex);
        Assert.False(guard.LowPowerEnabled);
        Assert.DoesNotContain(555, freezer.SuspendedPids);
        Assert.False(File.Exists(scope.Paths.SessionFile));

        // Audio entry stays preserved for GUI relaunch!
        var loaded = Assert.IsType<BekkyWake.Core.Store.JournalLoadResult.Loaded>(scope.Store.LoadState());
        Assert.Single(loaded.State.SavedAudioOutputs);
        Assert.False(loaded.State.IsDirtyApartFromAppOnlyEntries);
    }

    [Fact]
    public void Backstop_DoesNotSignalRecycledPidWithDifferentCreationTimestamp()
    {
        using var scope = new TempHomeScope();
        var clock = new FakeClock();
        var guard = new FakeSleepGuard();
        var freezer = new FakeProcessFreezer();

        // PID 888 was recycled with a newer CreationTimeTicks (99999 instead of 11111).
        freezer.Running.Add(new RunningAppInfo(888, "unrelatedApp"));
        freezer.CreationTicks[888] = 99999;
        freezer.SuspendedPids.Add(888);

        scope.Store.SaveSession(new Session(clock.Now.AddHours(-1), clock.Now.AddMinutes(-1)));
        scope.Store.SaveState(new RuntimeState
        {
            FrozenProcesses = [new FrozenProcess(888, "slack", new ProcessIdentity(11111, "boot-1"))]
        });

        var runner = new BackstopRunner(scope.Store, scope.RecoveryLock, clock, guard, freezer, scope.Log);
        var status = runner.Run();

        Assert.Equal(BackstopStatus.Recovered, status);
        // Unrelated recycled process must NEVER be signaled by backstop!
        Assert.Contains(888, freezer.SuspendedPids);
    }

    [Fact]
    public void Backstop_ReturnsLockBusyWhenAppHoldsRecoveryLock()
    {
        using var scope = new TempHomeScope();
        using var held = scope.RecoveryLock.TryAcquire();
        Assert.NotNull(held);

        var runner = new BackstopRunner(scope.Store, scope.RecoveryLock, new FakeClock(), new FakeSleepGuard(), new FakeProcessFreezer(), scope.Log);
        Assert.Equal(BackstopStatus.LockBusy, runner.Run());
    }
}
