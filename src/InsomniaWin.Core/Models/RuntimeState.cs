using System.Text.Json.Serialization;

namespace InsomniaWin.Core.Models;

/// <summary>
/// Kernel identity for a suspended process: exact creation timestamp + boot session ID.
/// Prevents signaling a recycled PID or a PID from an earlier Windows boot.
/// </summary>
public sealed record ProcessIdentity(long CreationTimeUtcTicks, string BootSessionId);

/// <summary>
/// One journaled process suspension. Provisional entries written before NtSuspendProcess
/// have Identity = null and are NEVER auto-resumed if the app dies mid-transition.
/// </summary>
public sealed record FrozenProcess(int Pid, string ProcessName, ProcessIdentity? Identity);

/// <summary>
/// One audio output muted by InsomniaWin when the lid closed, keyed by Core Audio Endpoint ID.
/// </summary>
public sealed record SavedAudioOutput(
    string DeviceId,
    string? FriendlyName,
    float? Volume,
    bool Muted,
    string SaveId);

/// <summary>
/// Original Windows power scheme LIDACTION indices before InsomniaWin changed them to Do Nothing (0).
/// </summary>
public sealed record SavedLidAction(
    Guid SchemeGuid,
    uint OriginalAcIndex,
    uint OriginalDcIndex);

/// <summary>
/// Original Windows Energy Saver threshold and Power Overlay scheme before Low Power Mode was enabled.
/// </summary>
public sealed record SavedPowerMode(
    Guid SchemeGuid,
    uint? OriginalEnergySaverDcThreshold,
    Guid? OriginalOverlayScheme);

/// <summary>
/// Write-ahead recovery journal persisted as state.json.
/// Every mutation is journaled BEFORE it is applied, and cleared ONLY after undo succeeds.
/// </summary>
public sealed class RuntimeState
{
    public bool SleepDisabledByUs { get; set; }
    public SavedLidAction? SavedLidAction { get; set; }

    public bool LowPowerSetByUs { get; set; }
    public SavedPowerMode? SavedPowerMode { get; set; }

    public List<FrozenProcess> FrozenProcesses { get; set; } = [];
    public bool DockerFrozen { get; set; }

    public List<SavedAudioOutput> SavedAudioOutputs { get; set; } = [];
    public byte? SavedDisplayBrightness { get; set; }
    public bool DisplayDarkenedByUs { get; set; }

    [JsonIgnore]
    public bool IsDirty =>
        SleepDisabledByUs
        || SavedLidAction is not null
        || LowPowerSetByUs
        || SavedPowerMode is not null
        || FrozenProcesses.Count > 0
        || DockerFrozen
        || SavedAudioOutputs.Count > 0
        || SavedDisplayBrightness.HasValue
        || DisplayDarkenedByUs;

    /// <summary>
    /// True if anything other than GUI-app-only restorations (audio devices / WMI display)
    /// remains pending. The background Task Scheduler backstop handles everything else.
    /// </summary>
    [JsonIgnore]
    public bool IsDirtyApartFromAppOnlyEntries =>
        SleepDisabledByUs
        || SavedLidAction is not null
        || LowPowerSetByUs
        || SavedPowerMode is not null
        || FrozenProcesses.Count > 0
        || DockerFrozen;

    [JsonIgnore]
    public bool HasUnconfirmedFrozenProcesses => FrozenProcesses.Any(p => p.Identity is null);

    public bool HasWaitingAudioOutputs(IEnumerable<string> connectedDeviceIds)
    {
        var connected = new HashSet<string>(connectedDeviceIds, StringComparer.OrdinalIgnoreCase);
        return SavedAudioOutputs.Any(a => !connected.Contains(a.DeviceId));
    }

    public bool HasDirtyChangesOtherThanDisconnectedAudio(IEnumerable<string> connectedDeviceIds)
    {
        if (IsDirtyApartFromAppOnlyEntries || DisplayDarkenedByUs || SavedDisplayBrightness.HasValue)
            return true;

        var connected = new HashSet<string>(connectedDeviceIds, StringComparer.OrdinalIgnoreCase);
        return SavedAudioOutputs.Any(a => connected.Contains(a.DeviceId));
    }

    public RuntimeState Clone()
    {
        return new RuntimeState
        {
            SleepDisabledByUs = SleepDisabledByUs,
            SavedLidAction = SavedLidAction,
            LowPowerSetByUs = LowPowerSetByUs,
            SavedPowerMode = SavedPowerMode,
            FrozenProcesses = [.. FrozenProcesses],
            DockerFrozen = DockerFrozen,
            SavedAudioOutputs = [.. SavedAudioOutputs],
            SavedDisplayBrightness = SavedDisplayBrightness,
            DisplayDarkenedByUs = DisplayDarkenedByUs
        };
    }
}
