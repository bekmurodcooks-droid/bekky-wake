using BekkyWake.Core.Models;

namespace BekkyWake.Core.Core;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public interface ISleepGuard
{
    /// <summary>
    /// Ensures display/system awake assertions via SetThreadExecutionState.
    /// </summary>
    Task<bool> SetExecutionStateAsync(bool keepSystemAwake, bool keepDisplayAwake);

    /// <summary>
    /// Reads the current power scheme's AC and DC LIDACTION values.
    /// </summary>
    SavedLidAction? ReadLidAction();

    /// <summary>
    /// Writes AC and DC LIDACTION=0 (Do Nothing) on the specified or active scheme.
    /// </summary>
    bool DisableLidSleep(out SavedLidAction? previous);

    /// <summary>
    /// Restores LIDACTION indices from a journaled SavedLidAction.
    /// </summary>
    bool RestoreLidAction(SavedLidAction saved);

    /// <summary>
    /// Checks if current active scheme already has LIDACTION == 0 on AC or DC.
    /// </summary>
    bool IsLidSleepCurrentlyDisabled();

    /// <summary>
    /// Enables Windows Low Power Mode / Energy Saver, returning original state for journaling.
    /// </summary>
    bool EnableLowPowerMode(out SavedPowerMode? previous);

    /// <summary>
    /// Restores Windows Low Power Mode / Energy Saver to journaled values.
    /// </summary>
    bool RestoreLowPowerMode(SavedPowerMode? saved);

    /// <summary>
    /// Sets Wireless Adapter Power Saving Mode to Maximum Performance (0), returning prior AC/DC indices.
    /// </summary>
    bool EnableWifiMaxPerformance(out SavedWifiPowerMode? previous);

    /// <summary>
    /// Restores Wireless Adapter Power Saving Mode to journaled AC/DC indices.
    /// </summary>
    bool RestoreWifiPowerMode(SavedWifiPowerMode? saved);

    /// <summary>
    /// Requests immediate system suspend (Modern Standby / S3).
    /// </summary>
    bool SleepNow();
}

public interface IPowerMonitor
{
    PowerSnapshot Sample();
}

public interface ILidObserver : IDisposable
{
    bool IsLidClosed { get; }
    bool HasExternalDisplay { get; }
    bool IsSimulationBuild { get; }

    event Action<bool>? LidStateChanged;
    event Action? PowerStatusChanged;
    event Action? DisplayTopologyChanged;

    void Start();
}

public interface IDisplayController
{
    byte? ReadBrightness();
    bool SetBrightness(byte level);
}

public interface IAudioController
{
    IReadOnlyList<AudioEndpointInfo> GetActiveRenderEndpoints();
    IReadOnlySet<string> GetConnectedRenderDeviceIds();
    bool MuteEndpoint(string deviceId);
    bool RestoreEndpoint(SavedAudioOutput saved);
}

public interface IProcessFreezer
{
    string GetBootSessionId();
    IReadOnlyList<RunningAppInfo> ListRunningApps();
    ProcessIdentity? SuspendProcess(int pid);
    bool ResumeProcess(FrozenProcess frozen);
    bool IsProcessAliveMatching(FrozenProcess frozen);
}

public enum DockerCheckStatus
{
    NotRunning,
    Idle,
    Busy,
    Error
}

public interface IDockerController
{
    Task<DockerCheckStatus> CheckStatusAsync(CancellationToken ct = default);
    bool IsDockerRunning(out RunningAppInfo? mainApp);
    Task<bool> UnfreezeDockerAsync();
}

public interface IBackstopScheduler
{
    bool EnsureRegistered(string executablePath);
    bool RemoveRegistration();
    bool IsRegistered();
}

public interface INotifier
{
    void Post(string title, string body, bool isWarning = false);
}

public interface IBrowserController
{
    Task<IReadOnlyList<ThrottledBrowser>> DetectThrottledBrowsersAsync();
    Task<(bool Success, string Message)> RelaunchUnthrottledAsync(ThrottledBrowser browser);
}

public interface IScreenLocker
{
    bool Lock();
}

public interface IStartupManager
{
    bool IsEnabled();
    bool SetEnabled(bool enabled, string executablePath);
}
