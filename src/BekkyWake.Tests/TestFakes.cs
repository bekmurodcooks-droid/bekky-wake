using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using BekkyWake.Core.Store;

namespace BekkyWake.Tests;

internal sealed class TempHomeScope : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }
    public JsonStore Store { get; }
    public RecoveryLock RecoveryLock { get; }
    public RollingLogger Log { get; }

    public TempHomeScope()
    {
        Root = Path.Combine(Path.GetTempPath(), $"BekkyWakeTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureDirectories();
        Store = new JsonStore(Paths);
        RecoveryLock = new RecoveryLock(Paths);
        Log = new RollingLogger(Paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Ignore locked temp files on Windows teardown.
        }
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan delta) => Now += delta;
}

internal sealed class FakeSleepGuard : ISleepGuard
{
    public bool KeepSystemAwake { get; private set; }
    public bool KeepDisplayAwake { get; private set; }
    public SavedLidAction CurrentLidAction { get; set; } = new(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), 1, 1);
    public bool LowPowerEnabled { get; private set; }
    public int SleepNowCount { get; private set; }
    public bool FailDisableLid { get; set; }
    public bool FailRestoreLid { get; set; }
    public bool FailRestoreLowPower { get; set; }
    public List<string> CallLog { get; } = [];

    public Task<bool> SetExecutionStateAsync(bool keepSystemAwake, bool keepDisplayAwake)
    {
        KeepSystemAwake = keepSystemAwake;
        KeepDisplayAwake = keepDisplayAwake;
        CallLog.Add($"ExecState({keepSystemAwake},{keepDisplayAwake})");
        return Task.FromResult(true);
    }

    public SavedLidAction? ReadLidAction() => CurrentLidAction;

    public bool DisableLidSleep(out SavedLidAction? previous)
    {
        previous = CurrentLidAction;
        if (FailDisableLid) return false;
        CurrentLidAction = CurrentLidAction with { OriginalAcIndex = 0, OriginalDcIndex = 0 };
        CallLog.Add("DisableLidSleep");
        return true;
    }

    public bool RestoreLidAction(SavedLidAction saved)
    {
        if (FailRestoreLid) return false;
        CurrentLidAction = saved;
        CallLog.Add($"RestoreLidAction({saved.OriginalAcIndex},{saved.OriginalDcIndex})");
        return true;
    }

    public bool IsLidSleepCurrentlyDisabled() => CurrentLidAction.OriginalAcIndex == 0 || CurrentLidAction.OriginalDcIndex == 0;

    public bool EnableLowPowerMode(out SavedPowerMode? previous)
    {
        previous = new SavedPowerMode(CurrentLidAction.SchemeGuid, 20, null);
        LowPowerEnabled = true;
        CallLog.Add("EnableLowPower");
        return true;
    }

    public bool RestoreLowPowerMode(SavedPowerMode? saved)
    {
        if (FailRestoreLowPower) return false;
        LowPowerEnabled = false;
        CallLog.Add("RestoreLowPower");
        return true;
    }

    public bool SleepNow()
    {
        SleepNowCount++;
        CallLog.Add("SleepNow");
        return true;
    }
}

internal sealed class FakePowerMonitor : IPowerMonitor
{
    public PowerSnapshot NextSnapshot { get; set; } = new(new BatteryStatus.Reading(85), true, ThermalLevel.Nominal, 42.0);
    public PowerSnapshot Sample() => NextSnapshot;
}

internal sealed class FakeLidObserver : ILidObserver
{
    public bool IsLidClosed { get; set; }
    public bool HasExternalDisplay { get; set; }
    public bool IsSimulationBuild { get; set; }

    public event Action<bool>? LidStateChanged;
    public event Action? PowerStatusChanged;
    public event Action? DisplayTopologyChanged;

    public void Start() { }
    public void RaiseLid(bool closed) { IsLidClosed = closed; LidStateChanged?.Invoke(closed); }
    public void RaisePower() => PowerStatusChanged?.Invoke();
    public void RaiseDisplay(bool external) { HasExternalDisplay = external; DisplayTopologyChanged?.Invoke(); }
    public void Dispose() { }
}

internal sealed class FakeDisplayController : IDisplayController
{
    public byte? Brightness { get; set; } = 65;
    public bool DisplayOff { get; private set; }
    public byte? ReadBrightness() => Brightness;
    public bool SetBrightness(byte level) { Brightness = level; return true; }
    public bool TurnDisplayOff() { DisplayOff = true; return true; }
    public bool WakeDisplay() { DisplayOff = false; return true; }
}

internal sealed class FakeAudioController : IAudioController
{
    public List<AudioEndpointInfo> Endpoints { get; } = [];
    public HashSet<string> ConnectedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MutedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddConnected(string id, string name, float volume = 0.5f, bool muted = false)
    {
        Endpoints.Add(new AudioEndpointInfo(id, name, volume, muted));
        ConnectedIds.Add(id);
    }

    public IReadOnlyList<AudioEndpointInfo> GetActiveRenderEndpoints() =>
        Endpoints.Where(e => ConnectedIds.Contains(e.DeviceId)).ToList();

    public IReadOnlySet<string> GetConnectedRenderDeviceIds() => ConnectedIds;

    public bool MuteEndpoint(string deviceId)
    {
        MutedIds.Add(deviceId);
        return true;
    }

    public bool RestoreEndpoint(SavedAudioOutput saved)
    {
        if (!ConnectedIds.Contains(saved.DeviceId)) return false;
        if (!saved.Muted) MutedIds.Remove(saved.DeviceId);
        else MutedIds.Add(saved.DeviceId);
        return true;
    }
}

internal sealed class FakeProcessFreezer : IProcessFreezer
{
    public string BootSession { get; set; } = "boot-1";
    public List<RunningAppInfo> Running { get; } = [];
    public Dictionary<int, long> CreationTicks { get; } = [];
    public HashSet<int> SuspendedPids { get; } = [];
    public HashSet<int> FailSuspendPids { get; } = [];
    public HashSet<int> FailResumePids { get; } = [];

    public string GetBootSessionId() => BootSession;
    public IReadOnlyList<RunningAppInfo> ListRunningApps() => Running.ToList();

    public ProcessIdentity? SuspendProcess(int pid)
    {
        if (FailSuspendPids.Contains(pid)) return null;
        if (!CreationTicks.TryGetValue(pid, out var ticks)) ticks = 1000L + pid;
        SuspendedPids.Add(pid);
        return new ProcessIdentity(ticks, BootSession);
    }

    public bool IsProcessAliveMatching(FrozenProcess frozen)
    {
        if (frozen.Identity is null) return false;
        if (!string.Equals(frozen.Identity.BootSessionId, BootSession, StringComparison.Ordinal)) return false;
        return Running.Any(r => r.Pid == frozen.Pid)
            && CreationTicks.TryGetValue(frozen.Pid, out var t)
            && t == frozen.Identity.CreationTimeUtcTicks;
    }

    public bool ResumeProcess(FrozenProcess frozen)
    {
        if (!IsProcessAliveMatching(frozen)) return false;
        if (FailResumePids.Contains(frozen.Pid)) return false;
        SuspendedPids.Remove(frozen.Pid);
        return true;
    }
}

internal sealed class FakeDockerController : IDockerController
{
    public RunningAppInfo? DockerApp { get; set; }
    public DockerCheckStatus NextStatus { get; set; } = DockerCheckStatus.Idle;
    public bool IsDockerRunning(out RunningAppInfo? mainApp) { mainApp = DockerApp; return DockerApp is not null; }
    public Task<DockerCheckStatus> CheckStatusAsync(CancellationToken ct = default) => Task.FromResult(NextStatus);
    public Task<bool> UnfreezeDockerAsync() => Task.FromResult(true);
}

internal sealed class FakeBackstopScheduler : IBackstopScheduler
{
    public bool Registered { get; private set; }
    public bool EnsureRegistered(string executablePath) { Registered = true; return true; }
    public bool RemoveRegistration() { Registered = false; return true; }
    public bool IsRegistered() => Registered;
}

internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Body, bool IsWarning)> Messages { get; } = [];
    public void Post(string title, string body, bool isWarning = false) => Messages.Add((title, body, isWarning));
}

internal sealed class FakeBrowserController : IBrowserController
{
    public List<ThrottledBrowser> Throttled { get; } = [];
    public Task<IReadOnlyList<ThrottledBrowser>> DetectThrottledBrowsersAsync() =>
        Task.FromResult<IReadOnlyList<ThrottledBrowser>>(Throttled.ToList());
    public Task<(bool Success, string Message)> RelaunchUnthrottledAsync(ThrottledBrowser browser) =>
        Task.FromResult((true, "ok"));
}

internal sealed class FakeScreenLocker : IScreenLocker
{
    public int LockCount { get; private set; }
    public bool Lock() { LockCount++; return true; }
}

internal sealed class FakeStartupManager : IStartupManager
{
    public bool Enabled { get; private set; }
    public bool IsEnabled() => Enabled;
    public bool SetEnabled(bool enabled, string executablePath) { Enabled = enabled; return true; }
}
