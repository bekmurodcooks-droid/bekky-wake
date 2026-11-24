using BekkyWake.Core.Models;
using BekkyWake.Core.Store;

namespace BekkyWake.Core.Core;

public enum EndOutcome
{
    Restored,
    Incomplete,
    Locked,
    SessionRetained,
    JournalUnreadable
}

public sealed record ManagerSnapshot(
    Session? ActiveSession,
    Config Config,
    PowerSnapshot Power,
    bool IsLidClosed,
    bool HasExternalDisplay,
    bool IsLidActionsApplied,
    bool IsSimulationBuild,
    bool IsJournalUnreadable,
    bool PendingEnd,
    string? LastError,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SavedAudioOutput> WaitingAudioOutputs,
    IReadOnlyList<ThrottledBrowser> ThrottledBrowsers)
{
    public bool IsActive => ActiveSession is not null;
}

/// <summary>
/// Central coordinator for timed awake sessions, write-ahead journaling,
/// battery/thermal safety floors, reversible lid-close actions, and launch recovery.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private static readonly TimeSpan LidDebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FloorPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarningReminderThreshold = TimeSpan.FromMinutes(5);

    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly RecoveryLock _recoveryLock;
    private readonly IClock _clock;
    private readonly ISleepGuard _sleepGuard;
    private readonly IPowerMonitor _powerMonitor;
    private readonly ILidObserver _lidObserver;
    private readonly IAudioController _audio;
    private readonly IBackstopScheduler _backstopScheduler;
    private readonly INotifier _notifier;
    private readonly IBrowserController _browserController;
    private readonly IStartupManager _startupManager;
    private readonly LidActions _lidActions;
    private readonly IAppLogger _log;
    private readonly string _executablePath;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private Config _config = new();
    private Session? _session;
    private RuntimeState _state = new();
    private PowerSnapshot _lastPower = new(new BatteryStatus.NoBatteryDesktop(), true, ThermalLevel.Nominal);
    private bool _isLidClosed;
    private bool _hasExternalDisplay;
    private bool _lidActionsApplied;
    private bool _isJournalUnreadable;
    private bool _pendingEnd;
    private bool _wasHot;
    private LowPowerCause? _lastLowPowerCause;
    private bool _fiveMinuteWarningSent;
    private int _consecutiveBatteryMisses;
    private string? _lastError;
    private readonly List<string> _persistentWarnings = [];
    private readonly List<string> _transientWarnings = [];
    private IReadOnlyList<ThrottledBrowser> _throttledBrowsers = [];

    private CancellationTokenSource? _expiryCts;
    private CancellationTokenSource? _floorPollCts;
    private CancellationTokenSource? _lidDebounceCts;
    private CancellationTokenSource? _dockerCheckCts;
    private bool _disposed;

    public event Action<ManagerSnapshot>? StateChanged;
    public event Action<bool, EndReason?>? SessionActivityTransition;

    public SessionManager(
        AppPaths paths,
        JsonStore store,
        RecoveryLock recoveryLock,
        IClock clock,
        ISleepGuard sleepGuard,
        IPowerMonitor powerMonitor,
        ILidObserver lidObserver,
        IAudioController audio,
        IBackstopScheduler backstopScheduler,
        INotifier notifier,
        IBrowserController browserController,
        IStartupManager startupManager,
        LidActions lidActions,
        IAppLogger log,
        string? executablePath = null)
    {
        _paths = paths;
        _store = store;
        _recoveryLock = recoveryLock;
        _clock = clock;
        _sleepGuard = sleepGuard;
        _powerMonitor = powerMonitor;
        _lidObserver = lidObserver;
        _audio = audio;
        _backstopScheduler = backstopScheduler;
        _notifier = notifier;
        _browserController = browserController;
        _startupManager = startupManager;
        _lidActions = lidActions;
        _log = log;
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;

        _lidObserver.LidStateChanged += OnLidStateChanged;
        _lidObserver.PowerStatusChanged += OnPowerStatusChanged;
        _lidObserver.DisplayTopologyChanged += OnDisplayTopologyChanged;
    }

    public ManagerSnapshot CurrentSnapshot
    {
        get
        {
            var connectedAudio = SafeGetConnectedAudioIds();
            var waitingAudio = _state.SavedAudioOutputs
                .Where(a => !connectedAudio.Contains(a.DeviceId))
                .ToList();

            var allWarnings = _persistentWarnings
                .Concat(_transientWarnings)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new ManagerSnapshot(
                _session,
                _config.Clone(),
                _lastPower,
                _isLidClosed,
                _hasExternalDisplay,
                _lidActionsApplied,
                _lidObserver.IsSimulationBuild,
                _isJournalUnreadable,
                _pendingEnd,
                _lastError,
                allWarnings,
                waitingAudio,
                _throttledBrowsers);
        }
    }

    public async Task ReconcileAsync()
    {
        ManagerSnapshot snapshot;
        EndReason? endedDuringReconcile = null;

        await _gate.WaitAsync();
        try
        {
            _paths.EnsureDirectories();
            _config = _store.LoadConfig(out var cfgWarn);
            if (cfgWarn is not null) _persistentWarnings.Add(cfgWarn);

            var floorCorrection = _config.NormalizeFloors();
            if (floorCorrection is not null)
            {
                _log.Warn(floorCorrection);
                try { _store.SaveConfig(_config); } catch { /* ignore */ }
            }

            _isLidClosed = _lidObserver.IsLidClosed;
            _hasExternalDisplay = _lidObserver.HasExternalDisplay;
            _lastPower = SamplePowerInternal();

            using var lockHandle = await _recoveryLock.AcquireAsync(TimeSpan.FromSeconds(5));
            if (lockHandle is null)
            {
                _persistentWarnings.Add("Recovery lock is currently held by the background recovery task; retrying shortly.");
                _log.Warn("Reconcile deferred because recovery.lock was busy.");
                snapshot = CurrentSnapshot;
            }
            else
            {
                var journalResult = _store.LoadState();
                switch (journalResult)
                {
                    case JournalLoadResult.Clean:
                        _state = new RuntimeState();
                        _isJournalUnreadable = false;
                        break;
                    case JournalLoadResult.Loaded loaded:
                        _state = loaded.State;
                        _isJournalUnreadable = false;
                        break;
                    case JournalLoadResult.Unreadable bad:
                        _state = new RuntimeState();
                        _isJournalUnreadable = true;
                        _persistentWarnings.Add($"Recovery journal state.json is unreadable ({bad.Error}). Sessions are blocked until resolved.");
                        _log.Error($"Unreadable state.json at reconcile: {bad.Error}");
                        break;
                }

                var sessionResult = _store.LoadSession();
                var now = _clock.Now;

                if (sessionResult is SessionLoadResult.Unreadable unreadableSession)
                {
                    var moved = _store.QuarantineUnreadableSession(now);
                    _persistentWarnings.Add($"Corrupt session file quarantined to {Path.GetFileName(moved)} ({unreadableSession.Error}).");
                    _log.Warn($"Quarantined unreadable session.json to {moved}");
                    await RestoreDirtyStateLockedAsync(EndReason.BackstopRecovery);
                }
                else if (sessionResult is SessionLoadResult.Loaded loadedSession)
                {
                    if (loadedSession.Session.IsExpired(now))
                    {
                        _log.Info("Found expired session.json on startup; running recovery.");
                        await RestoreDirtyStateLockedAsync(EndReason.BackstopRecovery);
                        TryDeleteSessionFileLocked();
                    }
                    else if (_isJournalUnreadable)
                    {
                        _persistentWarnings.Add("Active session cannot be resumed because state.json is unreadable.");
                    }
                    else
                    {
                        _session = loadedSession.Session;
                        await _sleepGuard.SetExecutionStateAsync(true, EffectiveKeepDisplayAwake);
                        EnsureBackstopRegistered();
                        ArmSessionTimersLocked();
                        endedDuringReconcile = await EvaluateFloorsLockedAsync(lockHandle);

                        if (_session is not null && _state.SavedAudioOutputs.Count > 0 && (!_isLidClosed || _hasExternalDisplay))
                        {
                            var res = _lidActions.UndoLidClose(_state);
                            _transientWarnings.AddRange(res.Warnings);
                        }

                        if (_session is not null && _isLidClosed && !_hasExternalDisplay)
                        {
                            var res = await _lidActions.ApplyLidCloseAsync(_config, _state);
                            _transientWarnings.AddRange(res.Warnings);
                            _lidActionsApplied = true;
                        }

                        if (_session is not null)
                        {
                            _log.Info($"Adopted active session ending at {_session.EndsAt:O}.");
                        }
                    }
                }
                else
                {
                    if (_state.IsDirty)
                    {
                        _log.Info("Found dirty state.json without session.json; restoring leftovers.");
                        await RestoreDirtyStateLockedAsync(EndReason.BackstopRecovery);
                    }
                    else if (!_isJournalUnreadable && _sleepGuard.IsLidSleepCurrentlyDisabled())
                    {
                        _persistentWarnings.Add("Windows lid-close sleep is currently set to 'Do nothing' outside bekky-wake; leaving your setting untouched.");
                        _log.Warn("Detected external LIDACTION=0 at launch with clean state.");
                    }
                }

                snapshot = CurrentSnapshot;
            }
        }
        finally
        {
            _gate.Release();
        }

        await RefreshBrowsersAsync();
        NotifyStateChanged(snapshot);
        if (endedDuringReconcile is { } er) NotifySessionEnded(er);
    }

    public async Task<(bool Ok, string? Error)> TryStartAsync(TimeSpan duration)
    {
        ManagerSnapshot snapshot;
        var started = false;
        string? error = null;

        await _gate.WaitAsync();
        try
        {
            if (_isJournalUnreadable)
            {
                error = "Recovery journal state.json is unreadable; resolve it before starting a session.";
                _lastError = error;
                snapshot = CurrentSnapshot;
            }
            else if (_session is not null)
            {
                error = "A session is already running; extend it instead.";
                _lastError = error;
                snapshot = CurrentSnapshot;
            }
            else
            {
                var power = SamplePowerInternal();
                _lastPower = power;

                var preActions = FloorRules.Evaluate(
                    power.Battery,
                    power.IsCharging,
                    power.Thermal,
                    wasHot: false,
                    lidClosed: _isLidClosed && !_hasExternalDisplay,
                    lowPowerSetByUs: false,
                    _config);

                if (preActions.OfType<FloorAction.EndSession>().FirstOrDefault() is { } endAction)
                {
                    error = $"Cannot start: {endAction.Reason.Description()}.";
                    _lastError = error;
                    snapshot = CurrentSnapshot;
                }
                else
                {
                    using var lockHandle = await _recoveryLock.AcquireAsync(TimeSpan.FromSeconds(5));
                    if (lockHandle is null)
                    {
                        error = "Recovery lock is busy with another operation; try again in a moment.";
                        _lastError = error;
                        snapshot = CurrentSnapshot;
                    }
                    else
                    {
                        _lastError = null;
                        _transientWarnings.Clear();
                        _wasHot = power.Thermal is ThermalLevel.Serious or ThermalLevel.Critical;
                        _fiveMinuteWarningSent = false;

                        var now = _clock.Now;
                        var newSession = SessionMath.NewSession(now, duration.TotalSeconds, _config.MaxDuration);

                        // Write-ahead journal BEFORE modifying Windows LIDACTION!
                        var originalLid = _sleepGuard.ReadLidAction();
                        _state.SleepDisabledByUs = true;
                        _state.SavedLidAction ??= originalLid;
                        _store.SaveState(_state);

                        if (!_sleepGuard.DisableLidSleep(out var confirmedPrev))
                        {
                            _state.SleepDisabledByUs = false;
                            _state.SavedLidAction = null;
                            try { _store.SaveState(_state); } catch { /* best effort */ }
                            error = "Could not disable lid-close sleep via Windows Power API.";
                            _lastError = error;
                            snapshot = CurrentSnapshot;
                        }
                        else
                        {
                            if (confirmedPrev is not null && _state.SavedLidAction is null)
                            {
                                _state.SavedLidAction = confirmedPrev;
                                _store.SaveState(_state);
                            }

                            if (_sleepGuard.EnableWifiMaxPerformance(out var prevWifi) && prevWifi is not null)
                            {
                                _state.SavedWifiPowerMode ??= prevWifi;
                                _store.SaveState(_state);
                            }

                            await _sleepGuard.SetExecutionStateAsync(true, EffectiveKeepDisplayAwake);
                            _store.SaveSession(newSession);
                            _session = newSession;
                            EnsureBackstopRegistered();
                            ArmSessionTimersLocked();

                            await EvaluateFloorsLockedAsync(lockHandle);
                            if (_session is not null && _isLidClosed && !_hasExternalDisplay)
                            {
                                var lidRes = await _lidActions.ApplyLidCloseAsync(_config, _state);
                                _transientWarnings.AddRange(lidRes.Warnings);
                                _lidActionsApplied = true;
                            }

                            started = _session is not null;
                            if (started)
                            {
                                _log.Info($"Started session for {SessionMath.FormatRemaining(newSession.RemainingSeconds(now))} (until {newSession.EndsAt:O}).");
                            }
                            snapshot = CurrentSnapshot;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _lastError = error;
            _log.Error($"TryStart failed: {ex.Message}");
            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }

        NotifyStateChanged(snapshot);
        if (started)
        {
            SessionActivityTransition?.Invoke(true, null);
            await RefreshBrowsersAsync();
        }
        return (started, error);
    }

    public async Task<(bool Ok, string? Error)> TryExtendAsync(TimeSpan extra)
    {
        ManagerSnapshot snapshot;
        var ok = false;
        string? error = null;

        await _gate.WaitAsync();
        try
        {
            if (_session is null)
            {
                error = "No session is running.";
                _lastError = error;
                snapshot = CurrentSnapshot;
                goto Done;
            }

            var now = _clock.Now;
            var extended = SessionMath.Extended(_session, extra.TotalSeconds, now, _config.MaxDuration);
            _store.SaveSession(extended);
            _session = extended;
            _fiveMinuteWarningSent = extended.Remaining(now) <= WarningReminderThreshold;
            _lastError = null;
            ArmSessionTimersLocked();
            ok = true;
            _log.Info($"Extended session; new end is {_session.EndsAt:O}.");
            snapshot = CurrentSnapshot;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _lastError = error;
            _log.Error($"TryExtend failed: {ex.Message}");
            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }

    Done:
        NotifyStateChanged(snapshot);
        return (ok, error);
    }

    public async Task<EndOutcome> EndAsync(EndReason reason)
    {
        ManagerSnapshot snapshot;
        EndOutcome outcome;
        bool endedNow;

        await _gate.WaitAsync();
        try
        {
            (outcome, endedNow) = await EndCoreLockedAsync(reason, existingLock: null);
            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }

        NotifyStateChanged(snapshot);
        if (endedNow)
        {
            NotifySessionEnded(reason);
        }
        return outcome;
    }

    public async Task StopWaitingForAudioAsync(string deviceId)
    {
        ManagerSnapshot snapshot;
        await _gate.WaitAsync();
        try
        {
            _state.SavedAudioOutputs.RemoveAll(a => string.Equals(a.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            _store.SaveState(_state);
            _log.Info($"Stopped waiting for disconnected audio device '{deviceId}'.");
            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }
        NotifyStateChanged(snapshot);
    }

    public async Task<(bool Ok, string Message)> RelaunchBrowserAsync(ThrottledBrowser browser)
    {
        var result = await _browserController.RelaunchUnthrottledAsync(browser);
        if (!result.Success)
        {
            await _gate.WaitAsync();
            try
            {
                _transientWarnings.Add(result.Message);
            }
            finally
            {
                _gate.Release();
            }
            _notifier.Post($"Could not relaunch {browser.DisplayName}", result.Message, isWarning: true);
        }

        await RefreshBrowsersAsync();
        return result;
    }

    public async Task RefreshBrowsersAsync()
    {
        var detected = await _browserController.DetectThrottledBrowsersAsync();
        ManagerSnapshot snapshot;
        await _gate.WaitAsync();
        try
        {
            _throttledBrowsers = detected;
            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }
        NotifyStateChanged(snapshot);
    }

    public async Task UpdateConfigAsync(Action<Config> mutate)
    {
        ManagerSnapshot snapshot;
        EndReason? endedReason = null;

        await _gate.WaitAsync();
        try
        {
            var prevKeepScreen = _config.KeepScreenAwake;
            var prevLaunchAtLogin = _config.LaunchAtLogin;

            mutate(_config);
            var floorNotice = _config.NormalizeFloors();
            if (floorNotice is not null) _log.Warn(floorNotice);
            _store.SaveConfig(_config);

            if (prevLaunchAtLogin != _config.LaunchAtLogin && !string.IsNullOrEmpty(_executablePath))
            {
                _startupManager.SetEnabled(_config.LaunchAtLogin, _executablePath);
            }

            if (_session is not null)
            {
                if (prevKeepScreen != _config.KeepScreenAwake)
                {
                    await _sleepGuard.SetExecutionStateAsync(true, EffectiveKeepDisplayAwake);
                }
                endedReason = await EvaluateFloorsLockedAsync();
            }

            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }

        NotifyStateChanged(snapshot);
        if (endedReason is { } er) NotifySessionEnded(er);
    }

    public bool CanQuit(out string reason)
    {
        if (_isJournalUnreadable)
        {
            reason = "Cannot quit while state.json is unreadable; inspect recovery warnings first.";
            return false;
        }

        var connected = SafeGetConnectedAudioIds();
        if (_state.HasDirtyChangesOtherThanDisconnectedAudio(connected))
        {
            reason = "Cannot quit yet: some session changes could not be undone.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void TickCountdown()
    {
        var session = _session;
        if (session is null) return;

        var now = _clock.Now;
        var remaining = session.Remaining(now);
        if (!_fiveMinuteWarningSent && remaining > TimeSpan.Zero && remaining <= WarningReminderThreshold)
        {
            _fiveMinuteWarningSent = true;
            _notifier.Post(
                "bekky-wake ending in 5 minutes",
                $"Session ends at {SessionMath.FormatEndTime(session.EndsAt, now)}.");
        }
    }

    private async Task<(EndOutcome Outcome, bool EndedNow)> EndCoreLockedAsync(EndReason reason, IDisposable? existingLock)
    {
        var hadSession = _session is not null;
        _pendingEnd = true;
        CancelSessionTimersLocked();

        if (_isJournalUnreadable)
        {
            _lastError = "state.json cannot be read, so nothing was undone.";
            _pendingEnd = false;
            return (EndOutcome.JournalUnreadable, false);
        }

        IDisposable? acquiredLock = null;
        if (existingLock is null)
        {
            acquiredLock = await _recoveryLock.AcquireAsync(TimeSpan.FromSeconds(5));
            if (acquiredLock is null)
            {
                _lastError = "Recovery lock is held by another process; retrying end shortly.";
                _pendingEnd = false;
                return (EndOutcome.Locked, false);
            }
        }

        try
        {
            var allClean = await RestoreDirtyStateLockedAsync(reason);
            await _sleepGuard.SetExecutionStateAsync(false, false);

            var sessionDeleted = TryDeleteSessionFileLocked();
            if (sessionDeleted)
            {
                _session = null;
            }

            _lidActionsApplied = false;
            _wasHot = false;
            _lastLowPowerCause = null;
            _fiveMinuteWarningSent = false;
            _pendingEnd = false;

            EndOutcome outcome;
            if (!sessionDeleted)
            {
                _lastError = "session.json could not be removed.";
                outcome = EndOutcome.SessionRetained;
            }
            else if (!allClean)
            {
                outcome = EndOutcome.Incomplete;
            }
            else
            {
                _lastError = null;
                outcome = EndOutcome.Restored;
            }

            if (reason.ShouldSleepIfLidClosed() && _isLidClosed && !_hasExternalDisplay && outcome == EndOutcome.Restored)
            {
                _log.Info($"Session ended ({reason}) while lid closed; putting system to sleep.");
                _sleepGuard.SleepNow();
            }

            return (outcome, hadSession && _session is null);
        }
        finally
        {
            acquiredLock?.Dispose();
        }
    }

    private async Task<bool> RestoreDirtyStateLockedAsync(EndReason reason)
    {
        var lidWarnings = _lidActions.UndoLidClose(_state).Warnings;
        _transientWarnings.AddRange(lidWarnings);

        var ok = true;
        if (_state.LowPowerSetByUs || _state.SavedPowerMode is not null)
        {
            if (_sleepGuard.RestoreLowPowerMode(_state.SavedPowerMode))
            {
                _state.LowPowerSetByUs = false;
                _state.SavedPowerMode = null;
                _store.SaveState(_state);
                _log.Info("Restored power mode.");
            }
            else
            {
                ok = false;
                _transientWarnings.Add("Could not restore Windows Low Power Mode.");
            }
        }

        if (_state.SavedWifiPowerMode is { } savedWifi)
        {
            if (_sleepGuard.RestoreWifiPowerMode(savedWifi))
            {
                _state.SavedWifiPowerMode = null;
                _store.SaveState(_state);
                _log.Info("Restored wireless adapter power saving mode.");
            }
            else
            {
                ok = false;
                _transientWarnings.Add("Could not restore wireless adapter power saving mode.");
            }
        }

        if (_state.SleepDisabledByUs || _state.SavedLidAction is not null)
        {
            if (_state.SavedLidAction is { } savedLid)
            {
                if (_sleepGuard.RestoreLidAction(savedLid))
                {
                    _state.SleepDisabledByUs = false;
                    _state.SavedLidAction = null;
                    _store.SaveState(_state);
                    _log.Info("Restored Windows lid close action.");
                }
                else
                {
                    ok = false;
                    _transientWarnings.Add("Could not restore Windows lid close power setting.");
                }
            }
            else
            {
                _state.SleepDisabledByUs = false;
                _store.SaveState(_state);
            }
        }

        var connected = SafeGetConnectedAudioIds();
        if (_state.HasDirtyChangesOtherThanDisconnectedAudio(connected))
        {
            ok = false;
        }

        _log.Info($"State restore pass ({reason}) finished. Clean={ok}");
        return await Task.FromResult(ok);
    }

    private async Task<EndReason?> EvaluateFloorsLockedAsync(IDisposable? existingLock = null)
    {
        if (_session is null) return null;

        var power = _lastPower;
        if (power.Thermal is ThermalLevel.Serious or ThermalLevel.Critical)
        {
            _wasHot = true;
        }
        else if (power.Thermal == ThermalLevel.Nominal)
        {
            _wasHot = false;
        }

        var effectiveLidClosed = _isLidClosed && !_hasExternalDisplay;
        var actions = FloorRules.Evaluate(
            power.Battery,
            power.IsCharging,
            power.Thermal,
            _wasHot,
            effectiveLidClosed,
            _state.LowPowerSetByUs,
            _config);

        foreach (var action in actions)
        {
            if (_session is null) break;

            switch (action)
            {
                case FloorAction.EndSession end:
                    var (_, endedNow) = await EndCoreLockedAsync(end.Reason, existingLock);
                    return endedNow ? end.Reason : null;

                case FloorAction.EnableLowPower enable:
                    _state.LowPowerSetByUs = true;
                    _store.SaveState(_state);
                    if (_sleepGuard.EnableLowPowerMode(out var prevPower))
                    {
                        _state.SavedPowerMode ??= prevPower;
                        _store.SaveState(_state);
                        _lastLowPowerCause = enable.Cause;
                        AnnounceLowPowerOn(enable.Cause, power);
                    }
                    else
                    {
                        _state.LowPowerSetByUs = false;
                        _store.SaveState(_state);
                    }
                    break;

                case FloorAction.DisableLowPower:
                    if (_sleepGuard.RestoreLowPowerMode(_state.SavedPowerMode))
                    {
                        var cause = _lastLowPowerCause;
                        _lastLowPowerCause = null;
                        _state.LowPowerSetByUs = false;
                        _state.SavedPowerMode = null;
                        _store.SaveState(_state);
                        AnnounceLowPowerOff(cause, power);
                    }
                    break;
            }
        }

        return null;
    }

    private void NotifySessionEnded(EndReason reason)
    {
        SessionActivityTransition?.Invoke(false, reason);
        if (reason is EndReason.BatteryFloor or EndReason.BatteryUnreadable or EndReason.ThermalCritical)
        {
            _notifier.Post("bekky-wake session ended", reason.Description(), isWarning: true);
        }
    }

    private void AnnounceLowPowerOn(LowPowerCause cause, PowerSnapshot power)
    {
        switch (cause)
        {
            case LowPowerCause.Thermal:
                _notifier.Post("Low Power Mode enabled", "Device temperature reached Serious level.");
                break;
            case LowPowerCause.Battery:
                _notifier.Post("Low Power Mode enabled", $"Battery at {power.Battery.PercentOrNull ?? 0}%, below {_config.LowPowerFloor}% floor.");
                break;
            case LowPowerCause.Lid:
                _log.Info("Low Power Mode enabled (lid closed).");
                break;
        }
    }

    private void AnnounceLowPowerOff(LowPowerCause? cause, PowerSnapshot power)
    {
        switch (cause)
        {
            case LowPowerCause.Lid:
                _log.Info("Low Power Mode restored (lid opened).");
                break;
            case LowPowerCause.Thermal:
                _notifier.Post("Low Power Mode restored", "Device temperature returned to normal.");
                break;
            case LowPowerCause.Battery:
                _notifier.Post("Low Power Mode restored", power.IsCharging ? "Charger connected." : "Battery above floor.");
                break;
        }
    }

    private void OnLidStateChanged(bool closed)
    {
        _lidDebounceCts?.Cancel();
        _lidDebounceCts = new CancellationTokenSource();
        var token = _lidDebounceCts.Token;

        if (!closed)
        {
            _dockerCheckCts?.Cancel();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LidDebounceDelay, token);
                await HandleEffectiveLidTransitionAsync(closed, _lidObserver.HasExternalDisplay);
            }
            catch (OperationCanceledException) { /* debounced */ }
        }, token);
    }

    private void OnDisplayTopologyChanged()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await HandleEffectiveLidTransitionAsync(_lidObserver.IsLidClosed, _lidObserver.HasExternalDisplay);
        });
    }

    private void OnPowerStatusChanged()
    {
        _ = Task.Run(async () =>
        {
            ManagerSnapshot snapshot;
            EndReason? endedReason = null;
            await _gate.WaitAsync();
            try
            {
                _lastPower = SamplePowerInternal();
                if (_session is not null)
                {
                    endedReason = await EvaluateFloorsLockedAsync();
                }
                snapshot = CurrentSnapshot;
            }
            finally
            {
                _gate.Release();
            }
            NotifyStateChanged(snapshot);
            if (endedReason is { } er) NotifySessionEnded(er);
        });
    }

    private async Task HandleEffectiveLidTransitionAsync(bool lidClosed, bool hasExternalDisplay)
    {
        ManagerSnapshot snapshot;
        EndReason? endedReason = null;
        await _gate.WaitAsync();
        try
        {
            _isLidClosed = lidClosed;
            _hasExternalDisplay = hasExternalDisplay;

            if (_session is null)
            {
                snapshot = CurrentSnapshot;
                goto Done;
            }

            await _sleepGuard.SetExecutionStateAsync(true, EffectiveKeepDisplayAwake);
            var shouldApply = lidClosed && !hasExternalDisplay;
            if (shouldApply && !_lidActionsApplied)
            {
                _dockerCheckCts?.Cancel();
                _dockerCheckCts = new CancellationTokenSource();
                var res = await _lidActions.ApplyLidCloseAsync(_config, _state, _dockerCheckCts.Token);
                _transientWarnings.AddRange(res.Warnings);
                _lidActionsApplied = true;
                endedReason = await EvaluateFloorsLockedAsync();
            }
            else if (!shouldApply && _lidActionsApplied)
            {
                _dockerCheckCts?.Cancel();
                var res = _lidActions.UndoLidClose(_state);
                _transientWarnings.AddRange(res.Warnings);
                _lidActionsApplied = false;
                endedReason = await EvaluateFloorsLockedAsync();
            }

            snapshot = CurrentSnapshot;
        }
        finally
        {
            _gate.Release();
        }

    Done:
        NotifyStateChanged(snapshot);
        if (endedReason is { } er) NotifySessionEnded(er);
    }

    private bool EffectiveKeepDisplayAwake => _config.KeepScreenAwake || (_isLidClosed && !_hasExternalDisplay);

    private PowerSnapshot SamplePowerInternal()
    {
        var raw = _powerMonitor.Sample();
        if (raw.Battery is BatteryStatus.Unreadable)
        {
            _consecutiveBatteryMisses++;
            return raw with { Battery = new BatteryStatus.Unreadable(_consecutiveBatteryMisses) };
        }

        _consecutiveBatteryMisses = 0;
        return raw;
    }

    private void ArmSessionTimersLocked()
    {
        CancelSessionTimersLocked();
        if (_session is null) return;

        var delay = _session.Remaining(_clock.Now);
        _expiryCts = new CancellationTokenSource();
        var expToken = _expiryCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, expToken);
                }
                if (!expToken.IsCancellationRequested)
                {
                    await EndAsync(EndReason.Timer);
                }
            }
            catch (OperationCanceledException) { /* cancelled */ }
        }, expToken);

        _floorPollCts = new CancellationTokenSource();
        var pollToken = _floorPollCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(FloorPollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(pollToken))
                {
                    ManagerSnapshot snapshot;
                    EndReason? endedReason = null;
                    await _gate.WaitAsync(pollToken);
                    try
                    {
                        _lastPower = SamplePowerInternal();
                        if (_session is not null)
                        {
                            endedReason = await EvaluateFloorsLockedAsync();
                        }
                        snapshot = CurrentSnapshot;
                    }
                    finally
                    {
                        _gate.Release();
                    }
                    NotifyStateChanged(snapshot);
                    if (endedReason is { } er) NotifySessionEnded(er);
                }
            }
            catch (OperationCanceledException) { /* cancelled */ }
        }, pollToken);
    }

    private void CancelSessionTimersLocked()
    {
        _expiryCts?.Cancel();
        _expiryCts?.Dispose();
        _expiryCts = null;

        _floorPollCts?.Cancel();
        _floorPollCts?.Dispose();
        _floorPollCts = null;
    }

    private void EnsureBackstopRegistered()
    {
        if (string.IsNullOrEmpty(_executablePath)) return;
        try
        {
            if (!_backstopScheduler.EnsureRegistered(_executablePath))
            {
                _log.Warn("Could not register Windows Task Scheduler backstop.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Backstop registration failed: {ex.Message}");
        }
    }

    private bool TryDeleteSessionFileLocked()
    {
        try
        {
            _store.DeleteSession();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to delete session.json: {ex.Message}");
            return false;
        }
    }

    private IReadOnlySet<string> SafeGetConnectedAudioIds()
    {
        try
        {
            return _audio.GetConnectedRenderDeviceIds();
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void NotifyStateChanged(ManagerSnapshot snapshot)
    {
        StateChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lidObserver.LidStateChanged -= OnLidStateChanged;
        _lidObserver.PowerStatusChanged -= OnPowerStatusChanged;
        _lidObserver.DisplayTopologyChanged -= OnDisplayTopologyChanged;
        CancelSessionTimersLocked();
        _lidDebounceCts?.Cancel();
        _dockerCheckCts?.Cancel();
        _gate.Dispose();
    }
}
