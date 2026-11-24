using BekkyWake.Core.Models;
using BekkyWake.Core.Store;

namespace BekkyWake.Core.Core;

public enum BackstopStatus
{
    CleanOrActive,
    Recovered,
    LockBusy,
    JournalUnreadable,
    PartialFailure
}

/// <summary>
/// Headless crash-recovery runner invoked every minute via Windows Task Scheduler (--backstop)
/// or standalone (--resume-frozen). Never touches valid unexpired sessions.
/// </summary>
public sealed class BackstopRunner
{
    private readonly JsonStore _store;
    private readonly RecoveryLock _recoveryLock;
    private readonly IClock _clock;
    private readonly ISleepGuard _sleepGuard;
    private readonly IProcessFreezer _freezer;
    private readonly IAppLogger _log;

    public BackstopRunner(
        JsonStore store,
        RecoveryLock recoveryLock,
        IClock clock,
        ISleepGuard sleepGuard,
        IProcessFreezer freezer,
        IAppLogger log)
    {
        _store = store;
        _recoveryLock = recoveryLock;
        _clock = clock;
        _sleepGuard = sleepGuard;
        _freezer = freezer;
        _log = log;
    }

    public BackstopStatus Run()
    {
        using var lockHandle = _recoveryLock.TryAcquire();
        if (lockHandle is null)
        {
            _log.Info("Backstop skipped: recovery.lock is held.");
            return BackstopStatus.LockBusy;
        }

        var now = _clock.Now;
        var sessionResult = _store.LoadSession();
        var isCorruptSession = sessionResult is SessionLoadResult.Unreadable;
        var isExpiredSession = sessionResult is SessionLoadResult.Loaded l && l.Session.IsExpired(now);

        if (sessionResult is SessionLoadResult.Loaded active && !active.Session.IsExpired(now))
        {
            // Valid active session: backstop leaves everything untouched.
            return BackstopStatus.CleanOrActive;
        }

        var journalResult = _store.LoadState();
        if (journalResult is JournalLoadResult.Unreadable bad)
        {
            _log.Error($"Backstop cannot read state.json: {bad.Error}");
            return BackstopStatus.JournalUnreadable;
        }

        var state = journalResult is JournalLoadResult.Loaded loaded
            ? loaded.State
            : new RuntimeState();

        if (!isCorruptSession && !isExpiredSession && !state.IsDirty)
        {
            return BackstopStatus.CleanOrActive;
        }

        _log.Warn($"Backstop running recovery (corruptSession={isCorruptSession}, expiredSession={isExpiredSession}, dirtyState={state.IsDirty}).");

        if (isCorruptSession)
        {
            try
            {
                var moved = _store.QuarantineUnreadableSession(now);
                _log.Warn($"Backstop quarantined unreadable session.json to {moved}");
            }
            catch (Exception ex)
            {
                _log.Error($"Backstop failed to quarantine session.json: {ex.Message}");
                return BackstopStatus.PartialFailure;
            }
        }

        var allBackstopClean = true;

        // 1. Restore Low Power Mode.
        if (state.LowPowerSetByUs || state.SavedPowerMode is not null)
        {
            if (_sleepGuard.RestoreLowPowerMode(state.SavedPowerMode))
            {
                state.LowPowerSetByUs = false;
                state.SavedPowerMode = null;
                _store.SaveState(state);
                _log.Info("Backstop restored power mode.");
            }
            else
            {
                allBackstopClean = false;
                _log.Error("Backstop failed to restore power mode.");
            }
        }

        // 2. Restore Windows LIDACTION.
        if (state.SleepDisabledByUs || state.SavedLidAction is not null)
        {
            if (state.SavedLidAction is { } savedLid)
            {
                if (_sleepGuard.RestoreLidAction(savedLid))
                {
                    state.SleepDisabledByUs = false;
                    state.SavedLidAction = null;
                    _store.SaveState(state);
                    _log.Info("Backstop restored LIDACTION.");
                }
                else
                {
                    allBackstopClean = false;
                    _log.Error("Backstop failed to restore LIDACTION.");
                }
            }
            else
            {
                state.SleepDisabledByUs = false;
                _store.SaveState(state);
            }
        }

        // 3. Resume verified frozen processes (never resume provisional Identity=null or recycled PIDs).
        if (state.FrozenProcesses.Count > 0 || state.DockerFrozen)
        {
            var remaining = new List<FrozenProcess>();
            foreach (var proc in state.FrozenProcesses)
            {
                if (proc.Identity is null)
                {
                    remaining.Add(proc);
                    allBackstopClean = false;
                    _log.Warn($"Backstop left unconfirmed freeze {proc.ProcessName} (PID {proc.Pid}) untouched.");
                    continue;
                }

                if (!_freezer.IsProcessAliveMatching(proc))
                {
                    continue;
                }

                if (_freezer.ResumeProcess(proc))
                {
                    _log.Info($"Backstop resumed {proc.ProcessName} (PID {proc.Pid}).");
                }
                else
                {
                    remaining.Add(proc);
                    allBackstopClean = false;
                    _log.Error($"Backstop failed to resume {proc.ProcessName} (PID {proc.Pid}).");
                }
            }

            state.FrozenProcesses = remaining;
            if (!remaining.Any(p => FreezePlanner.NormalizeName(p.ProcessName).Contains("docker", StringComparison.OrdinalIgnoreCase)))
            {
                state.DockerFrozen = false;
            }
            _store.SaveState(state);
        }

        // Note: SavedAudioOutputs and SavedDisplayBrightness stay in state.json for the GUI app to restore on next launch.
        if (isExpiredSession && !state.IsDirtyApartFromAppOnlyEntries)
        {
            try
            {
                _store.DeleteSession();
                _log.Info("Backstop deleted expired session.json.");
            }
            catch (Exception ex)
            {
                _log.Error($"Backstop could not delete expired session.json: {ex.Message}");
                return BackstopStatus.PartialFailure;
            }
        }

        return allBackstopClean && !state.IsDirtyApartFromAppOnlyEntries
            ? BackstopStatus.Recovered
            : BackstopStatus.PartialFailure;
    }

    public int ResumeVerifiedFrozenOnly()
    {
        using var lockHandle = _recoveryLock.TryAcquire();
        if (lockHandle is null) return 1;

        var journalResult = _store.LoadState();
        if (journalResult is not JournalLoadResult.Loaded loaded) return 0;

        var state = loaded.State;
        if (state.FrozenProcesses.Count == 0) return 0;

        var remaining = new List<FrozenProcess>();
        var allOk = true;
        foreach (var proc in state.FrozenProcesses)
        {
            if (proc.Identity is null)
            {
                remaining.Add(proc);
                allOk = false;
                continue;
            }

            if (!_freezer.IsProcessAliveMatching(proc)) continue;

            if (_freezer.ResumeProcess(proc))
            {
                _log.Info($"--resume-frozen resumed {proc.ProcessName} (PID {proc.Pid}).");
            }
            else
            {
                remaining.Add(proc);
                allOk = false;
            }
        }

        state.FrozenProcesses = remaining;
        if (!remaining.Any(p => FreezePlanner.NormalizeName(p.ProcessName).Contains("docker", StringComparison.OrdinalIgnoreCase)))
        {
            state.DockerFrozen = false;
        }
        _store.SaveState(state);
        return allOk ? 0 : 1;
    }
}
