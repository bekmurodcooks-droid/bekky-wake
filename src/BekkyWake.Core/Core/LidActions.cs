using BekkyWake.Core.Models;
using BekkyWake.Core.Store;

namespace BekkyWake.Core.Core;

public sealed class LidActions
{
    private readonly JsonStore _store;
    private readonly IDisplayController _display;
    private readonly IAudioController _audio;
    private readonly IProcessFreezer _freezer;
    private readonly IDockerController _docker;
    private readonly IScreenLocker _locker;
    private readonly IAppLogger _log;

    public LidActions(
        JsonStore store,
        IDisplayController display,
        IAudioController audio,
        IProcessFreezer freezer,
        IDockerController docker,
        IScreenLocker locker,
        IAppLogger log)
    {
        _store = store;
        _display = display;
        _audio = audio;
        _freezer = freezer;
        _docker = docker;
        _locker = locker;
        _log = log;
    }

    public async Task<LidActionResult> ApplyLidCloseAsync(
        Config config,
        RuntimeState state,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (config.LockScreenOnLidClose)
        {
            if (_locker.Lock())
            {
                _log.Info("Screen locked on lid close.");
            }
            else
            {
                warnings.Add("Could not lock the screen on lid close.");
                _log.Warn("LockWorkStation returned false on lid close.");
            }
        }

        if (config.DarkenDisplayOnLidClose && !state.DisplayDarkenedByUs)
        {
            var brightness = _display.ReadBrightness();
            state.SavedDisplayBrightness ??= brightness;
            state.DisplayDarkenedByUs = true;
            try
            {
                _store.SaveState(state);
                if (brightness.HasValue)
                {
                    _display.SetBrightness(0);
                }
                _display.TurnDisplayOff();
                _log.Info($"Display darkened on lid close (saved brightness: {brightness?.ToString() ?? "n/a"}).");
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to darken display: {ex.Message}");
                _log.Error($"Display darken failed: {ex.Message}");
            }
        }

        if (config.MuteOnLidClose)
        {
            try
            {
                var endpoints = _audio.GetActiveRenderEndpoints();
                var existingIds = new HashSet<string>(
                    state.SavedAudioOutputs.Select(x => x.DeviceId),
                    StringComparer.OrdinalIgnoreCase);

                var newlySaved = new List<SavedAudioOutput>();
                foreach (var ep in endpoints)
                {
                    if (existingIds.Contains(ep.DeviceId)) continue;
                    newlySaved.Add(new SavedAudioOutput(
                        ep.DeviceId,
                        ep.FriendlyName,
                        ep.Volume,
                        ep.Muted,
                        Guid.NewGuid().ToString("N")));
                }

                if (newlySaved.Count > 0)
                {
                    state.SavedAudioOutputs.AddRange(newlySaved);
                    _store.SaveState(state);
                }

                foreach (var ep in endpoints)
                {
                    if (!_audio.MuteEndpoint(ep.DeviceId))
                    {
                        _log.Warn($"Could not mute audio endpoint '{ep.FriendlyName}'.");
                    }
                }
                _log.Info($"Muted {endpoints.Count} active audio output(s) on lid close.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to mute audio: {ex.Message}");
                _log.Error($"Audio mute failed: {ex.Message}");
            }
        }

        // Freeze configured apps (write-ahead provisional entries before suspending).
        try
        {
            var running = _freezer.ListRunningApps();
            var targets = FreezePlanner.PlanFreezeTargets(running, config);
            FreezeTargets(targets, state, warnings, markDockerFrozen: false);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to freeze configured apps: {ex.Message}");
            _log.Error($"App freeze failed: {ex.Message}");
        }

        // Optional Docker Desktop idle freeze rule (two-step check with cancellation if lid opens).
        if (config.DockerRule && !ct.IsCancellationRequested && _docker.IsDockerRunning(out var dockerApp) && dockerApp is not null)
        {
            try
            {
                var first = await _docker.CheckStatusAsync(ct);
                if (first == DockerCheckStatus.Idle && !ct.IsCancellationRequested)
                {
                    var second = await _docker.CheckStatusAsync(ct);
                    if (second == DockerCheckStatus.Idle && !ct.IsCancellationRequested)
                    {
                        FreezeTargets([dockerApp], state, warnings, markDockerFrozen: true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _log.Info("Docker idle check cancelled because lid reopened.");
            }
            catch (Exception ex)
            {
                _log.Warn($"Docker idle check failed; leaving Docker running ({ex.Message}).");
            }
        }

        return new LidActionResult(warnings);
    }

    public LidActionResult UndoLidClose(RuntimeState state)
    {
        var warnings = new List<string>();

        if (state.DisplayDarkenedByUs || state.SavedDisplayBrightness.HasValue)
        {
            var savedBrightness = state.SavedDisplayBrightness;
            try
            {
                _display.WakeDisplay();
                if (savedBrightness.HasValue)
                {
                    _display.SetBrightness(savedBrightness.Value);
                }
                state.DisplayDarkenedByUs = false;
                state.SavedDisplayBrightness = null;
                _store.SaveState(state);
                _log.Info("Restored display state on lid open.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not restore display brightness: {ex.Message}");
                _log.Error($"Display restore failed: {ex.Message}");
            }
        }

        if (state.SavedAudioOutputs.Count > 0)
        {
            try
            {
                var connected = _audio.GetConnectedRenderDeviceIds();
                var remaining = new List<SavedAudioOutput>();
                foreach (var saved in state.SavedAudioOutputs)
                {
                    if (!connected.Contains(saved.DeviceId))
                    {
                        remaining.Add(saved);
                        continue;
                    }

                    if (_audio.RestoreEndpoint(saved))
                    {
                        _log.Info($"Restored audio device '{saved.FriendlyName ?? saved.DeviceId}'.");
                    }
                    else
                    {
                        remaining.Add(saved);
                        warnings.Add($"Could not restore audio for '{saved.FriendlyName ?? saved.DeviceId}'.");
                    }
                }

                if (remaining.Count != state.SavedAudioOutputs.Count)
                {
                    state.SavedAudioOutputs = remaining;
                    _store.SaveState(state);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Audio restore error: {ex.Message}");
                _log.Error($"Audio restore error: {ex.Message}");
            }
        }

        if (state.FrozenProcesses.Count > 0 || state.DockerFrozen)
        {
            ResumeVerifiedFrozen(state, warnings);
        }

        return new LidActionResult(warnings);
    }

    public void ResumeVerifiedFrozen(RuntimeState state, List<string> warnings)
    {
        var remaining = new List<FrozenProcess>();
        var anyChanged = false;

        foreach (var proc in state.FrozenProcesses)
        {
            if (proc.Identity is null)
            {
                remaining.Add(proc);
                warnings.Add($"Unconfirmed freeze for {proc.ProcessName} (PID {proc.Pid}); inspect manually before resuming.");
                continue;
            }

            var aliveMatch = _freezer.IsProcessAliveMatching(proc);
            if (!aliveMatch)
            {
                // Process exited naturally or PID was recycled — never signal recycled PID, drop stale entry.
                anyChanged = true;
                continue;
            }

            if (_freezer.ResumeProcess(proc))
            {
                anyChanged = true;
                _log.Info($"Resumed {proc.ProcessName} (PID {proc.Pid}).");
            }
            else
            {
                remaining.Add(proc);
                warnings.Add($"Could not resume {proc.ProcessName} (PID {proc.Pid}).");
                _log.Warn($"Failed to resume {proc.ProcessName} (PID {proc.Pid}).");
            }
        }

        if (anyChanged || state.DockerFrozen)
        {
            state.FrozenProcesses = remaining;
            if (!remaining.Any(p => FreezePlanner.NormalizeName(p.ProcessName).Contains("docker", StringComparison.OrdinalIgnoreCase)))
            {
                state.DockerFrozen = false;
            }
            try
            {
                _store.SaveState(state);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to update state.json after resuming processes: {ex.Message}");
                _log.Error($"SaveState after resume failed: {ex.Message}");
            }
        }
    }

    private void FreezeTargets(
        IReadOnlyList<RunningAppInfo> targets,
        RuntimeState state,
        List<string> warnings,
        bool markDockerFrozen)
    {
        var existingPids = state.FrozenProcesses.Select(p => p.Pid).ToHashSet();
        var candidates = targets.Where(t => !existingPids.Contains(t.Pid)).ToList();
        if (candidates.Count == 0) return;

        // Step 1: Journal provisional entries BEFORE calling NtSuspendProcess.
        var provisional = candidates
            .Select(c => new FrozenProcess(c.Pid, c.ProcessName, null))
            .ToList();

        state.FrozenProcesses.AddRange(provisional);
        if (markDockerFrozen) state.DockerFrozen = true;

        try
        {
            _store.SaveState(state);
        }
        catch (Exception ex)
        {
            state.FrozenProcesses.RemoveAll(p => provisional.Any(x => x.Pid == p.Pid && p.Identity is null));
            if (markDockerFrozen) state.DockerFrozen = false;
            warnings.Add($"Skipped freezing apps because journal write failed: {ex.Message}");
            _log.Error($"Provisional freeze journal write failed: {ex.Message}");
            return;
        }

        // Step 2: Suspend and record kernel ProcessIdentity.
        var confirmed = new List<FrozenProcess>();
        var failedPids = new HashSet<int>();

        foreach (var target in candidates)
        {
            var identity = _freezer.SuspendProcess(target.Pid);
            if (identity is not null)
            {
                confirmed.Add(new FrozenProcess(target.Pid, target.ProcessName, identity));
            }
            else
            {
                failedPids.Add(target.Pid);
            }
        }

        // Replace provisional entries with confirmed identities (or drop if suspend failed).
        state.FrozenProcesses.RemoveAll(p => provisional.Any(x => x.Pid == p.Pid && p.Identity is null));
        state.FrozenProcesses.AddRange(confirmed);
        if (markDockerFrozen && confirmed.Count == 0)
        {
            state.DockerFrozen = false;
        }

        // Step 3: Persist confirmed identities. If this write fails, roll back immediately!
        try
        {
            _store.SaveState(state);
            _log.Info($"Froze {confirmed.Count} process(es) with verified kernel identity.");
        }
        catch (Exception ex)
        {
            _log.Error($"Confirming freeze write failed ({ex.Message}); rolling back {confirmed.Count} suspended process(es).");
            foreach (var proc in confirmed)
            {
                _freezer.ResumeProcess(proc);
            }
            state.FrozenProcesses.RemoveAll(p => confirmed.Any(c => c.Pid == p.Pid));
            if (markDockerFrozen) state.DockerFrozen = false;
            warnings.Add($"Rolled back process freeze because journal confirmation failed: {ex.Message}");
        }
    }
}

public sealed record LidActionResult(IReadOnlyList<string> Warnings);
