using InsomniaWin.Core.Core;
using InsomniaWin.Core.Models;
using InsomniaWin.Core.Store;
using Xunit;

namespace InsomniaWin.Tests;

public sealed class SessionManagerTests
{
    private static (SessionManager Manager, FakeClock Clock, FakeSleepGuard Guard, FakePowerMonitor Power, FakeLidObserver Lid, FakeAudioController Audio, FakeProcessFreezer Freezer, FakeNotifier Notifier)
        CreateHarness(TempHomeScope scope)
    {
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

        var manager = new SessionManager(
            scope.Paths,
            scope.Store,
            scope.RecoveryLock,
            clock,
            guard,
            power,
            lid,
            audio,
            backstop,
            notifier,
            browsers,
            startup,
            lidActions,
            scope.Log,
            @"C:\Test\InsomniaWin.exe");

        return (manager, clock, guard, power, lid, audio, freezer, notifier);
    }

    [Fact]
    public async Task StartExtendEnd_RestoresWindowsSettingsAndDeletesSession()
    {
        using var scope = new TempHomeScope();
        var (mgr, clock, guard, _, _, _, _, _) = CreateHarness(scope);
        using (mgr)
        {
            await mgr.ReconcileAsync();
            var (ok, err) = await mgr.TryStartAsync(TimeSpan.FromHours(1));
            Assert.True(ok, err);
            Assert.True(guard.KeepSystemAwake);
            Assert.Equal(0u, guard.CurrentLidAction.OriginalAcIndex);
            Assert.Equal(0u, guard.CurrentLidAction.OriginalDcIndex);
            Assert.True(File.Exists(scope.Paths.SessionFile));

            var (extOk, _) = await mgr.TryExtendAsync(TimeSpan.FromMinutes(30));
            Assert.True(extOk);
            Assert.Equal(clock.Now.AddMinutes(90), mgr.CurrentSnapshot.ActiveSession!.EndsAt);

            var outcome = await mgr.EndAsync(EndReason.User);
            Assert.Equal(EndOutcome.Restored, outcome);
            Assert.False(guard.KeepSystemAwake);
            Assert.Equal(1u, guard.CurrentLidAction.OriginalAcIndex);
            Assert.Equal(1u, guard.CurrentLidAction.OriginalDcIndex);
            Assert.False(File.Exists(scope.Paths.SessionFile));
            Assert.Equal(0, guard.SleepNowCount); // User end does not force sleep.
        }
    }

    [Fact]
    public async Task TimerEndWhileLidClosedWithoutExternalMonitor_SleepsSystem()
    {
        using var scope = new TempHomeScope();
        var (mgr, _, guard, _, lid, _, _, _) = CreateHarness(scope);
        using (mgr)
        {
            lid.IsLidClosed = true;
            lid.HasExternalDisplay = false;
            await mgr.ReconcileAsync();

            var (ok, _) = await mgr.TryStartAsync(TimeSpan.FromMinutes(10));
            Assert.True(ok);

            var outcome = await mgr.EndAsync(EndReason.Timer);
            Assert.Equal(EndOutcome.Restored, outcome);
            Assert.Equal(1, guard.SleepNowCount);
        }
    }

    [Fact]
    public async Task Reconcile_QuarantinesCorruptSessionAndRecoversDirtyState()
    {
        using var scope = new TempHomeScope();
        File.WriteAllText(scope.Paths.SessionFile, "{ not valid json");
        scope.Store.SaveState(new RuntimeState
        {
            SleepDisabledByUs = true,
            SavedLidAction = new SavedLidAction(Guid.NewGuid(), 1, 2)
        });

        var (mgr, _, guard, _, _, _, _, _) = CreateHarness(scope);
        guard.CurrentLidAction = new SavedLidAction(guard.CurrentLidAction.SchemeGuid, 0, 0);

        using (mgr)
        {
            await mgr.ReconcileAsync();
            Assert.False(mgr.CurrentSnapshot.IsActive);
            Assert.False(File.Exists(scope.Paths.SessionFile));
            Assert.NotEmpty(Directory.GetFiles(scope.Root, "session.json.unreadable-*"));
            Assert.Equal(1u, guard.CurrentLidAction.OriginalAcIndex);
            Assert.Equal(2u, guard.CurrentLidAction.OriginalDcIndex);
            Assert.NotEmpty(mgr.CurrentSnapshot.Warnings);
        }
    }

    [Fact]
    public async Task UnreadableJournal_BlocksStartingSessionAndRefusesQuit()
    {
        using var scope = new TempHomeScope();
        File.WriteAllText(scope.Paths.StateFile, "{ broken state");

        var (mgr, _, _, _, _, _, _, _) = CreateHarness(scope);
        using (mgr)
        {
            await mgr.ReconcileAsync();
            Assert.True(mgr.CurrentSnapshot.IsJournalUnreadable);

            var (ok, err) = await mgr.TryStartAsync(TimeSpan.FromMinutes(30));
            Assert.False(ok);
            Assert.Contains("unreadable", err!, StringComparison.OrdinalIgnoreCase);
            Assert.False(mgr.CanQuit(out _));
        }
    }

    [Fact]
    public async Task CanQuit_AllowsDisconnectedWaitingAudioOutputs()
    {
        using var scope = new TempHomeScope();
        var (mgr, _, _, _, _, audio, _, _) = CreateHarness(scope);
        scope.Store.SaveState(new RuntimeState
        {
            SavedAudioOutputs = [new SavedAudioOutput("usb-dac", "USB DAC", 0.5f, false, "id-1")]
        });

        using (mgr)
        {
            await mgr.ReconcileAsync();
            Assert.Empty(audio.ConnectedIds);
            Assert.True(mgr.CanQuit(out _));
            Assert.Single(mgr.CurrentSnapshot.WaitingAudioOutputs);
        }
    }
}
