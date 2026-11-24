using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using BekkyWake.Core.Store;

namespace BekkyWake.Core.System;

public sealed class WindowsSleepGuard : ISleepGuard, IDisposable
{
    private readonly IAppLogger _log;
    private readonly BlockingCollection<(bool SystemAwake, bool DisplayAwake, TaskCompletionSource<bool> Tcs)> _queue = new();
    private readonly Thread _execThread;
    private bool _disposed;

    public WindowsSleepGuard(IAppLogger log)
    {
        _log = log;
        _execThread = new Thread(ExecutionThreadLoop)
        {
            IsBackground = true,
            Name = "BekkyWake_ExecutionState"
        };
        _execThread.Start();
    }

    private void ExecutionThreadLoop()
    {
        foreach (var (sysAwake, dispAwake, tcs) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var flags = NativeMethods.EXECUTION_STATE.ES_CONTINUOUS;
                if (sysAwake) flags |= NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED;
                if (dispAwake) flags |= NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED;
                var prev = NativeMethods.SetThreadExecutionState(flags);
                tcs.TrySetResult(prev != 0);
            }
            catch (Exception ex)
            {
                _log.Error($"SetThreadExecutionState failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        }
    }

    public Task<bool> SetExecutionStateAsync(bool keepSystemAwake, bool keepDisplayAwake)
    {
        if (_disposed) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((keepSystemAwake, keepDisplayAwake, tcs));
        return tcs.Task;
    }

    public SavedLidAction? ReadLidAction()
    {
        if (!TryGetActiveScheme(out var scheme)) return null;
        var sub = NativeMethods.SubgroupButtons;
        var set = NativeMethods.SettingLidAction;

        var rcAc = NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var ac);
        var rcDc = NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var dc);
        if (rcAc != 0 || rcDc != 0)
        {
            _log.Warn($"PowerReadValueIndex failed (ac={rcAc}, dc={rcDc}).");
            return null;
        }

        return new SavedLidAction(scheme, ac, dc);
    }

    public bool DisableLidSleep(out SavedLidAction? previous)
    {
        previous = ReadLidAction();
        if (previous is null) return false;

        var scheme = previous.SchemeGuid;
        var sub = NativeMethods.SubgroupButtons;
        var set = NativeMethods.SettingLidAction;

        var rcAc = NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0);
        var rcDc = NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0);
        var rcActive = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

        if (rcAc != 0 || rcDc != 0 || rcActive != 0)
        {
            _log.Error($"DisableLidSleep failed (ac={rcAc}, dc={rcDc}, active={rcActive}).");
            return false;
        }

        return true;
    }

    public bool RestoreLidAction(SavedLidAction saved)
    {
        var scheme = saved.SchemeGuid;
        var sub = NativeMethods.SubgroupButtons;
        var set = NativeMethods.SettingLidAction;

        var rcAc = NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, saved.OriginalAcIndex);
        var rcDc = NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, saved.OriginalDcIndex);
        var rcActive = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

        if (rcAc != 0 || rcDc != 0 || rcActive != 0)
        {
            _log.Error($"RestoreLidAction failed (ac={rcAc}, dc={rcDc}, active={rcActive}).");
            return false;
        }

        return true;
    }

    public bool IsLidSleepCurrentlyDisabled()
    {
        var current = ReadLidAction();
        return current is not null && (current.OriginalAcIndex == 0 || current.OriginalDcIndex == 0);
    }

    public bool EnableLowPowerMode(out SavedPowerMode? previous)
    {
        previous = null;
        if (!TryGetActiveScheme(out var scheme)) return false;

        uint? origThreshold = null;
        var sub = NativeMethods.SubgroupEnergySaver;
        var set = NativeMethods.SettingEnergySaverThreshold;
        if (NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var dcThresh) == 0)
        {
            origThreshold = dcThresh;
        }

        Guid? origOverlay = null;
        try
        {
            if (NativeMethods.PowerGetActualOverlayScheme(out var overlay) == 0)
            {
                origOverlay = overlay;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Overlay schemes aren't supported on all Windows configurations.
        }

        previous = new SavedPowerMode(scheme, origThreshold, origOverlay);

        var ok = true;
        if (origThreshold.HasValue)
        {
            var rcWrite = NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 100);
            var rcApply = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (rcWrite != 0 || rcApply != 0) ok = false;
        }

        if (origOverlay.HasValue)
        {
            try
            {
                NativeMethods.PowerSetActiveOverlayScheme(NativeMethods.OverlayBetterBattery);
            }
            catch (EntryPointNotFoundException)
            {
                // Ignore cleanly.
            }
        }

        return ok;
    }

    public bool RestoreLowPowerMode(SavedPowerMode? saved)
    {
        if (saved is null) return true;

        var ok = true;
        if (saved.OriginalEnergySaverDcThreshold is uint threshold)
        {
            var scheme = saved.SchemeGuid;
            var sub = NativeMethods.SubgroupEnergySaver;
            var set = NativeMethods.SettingEnergySaverThreshold;
            var rcWrite = NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, threshold);
            var rcApply = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (rcWrite != 0 || rcApply != 0) ok = false;
        }

        if (saved.OriginalOverlayScheme is Guid overlay)
        {
            try
            {
                NativeMethods.PowerSetActiveOverlayScheme(overlay);
            }
            catch (EntryPointNotFoundException)
            {
                // Ignore cleanly.
            }
        }

        return ok;
    }

    public bool SleepNow()
    {
        return NativeMethods.SetSuspendState(false, false, false);
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        var rc = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (rc != 0 || ptr == IntPtr.Zero) return false;
        try
        {
            scheme = Marshal.PtrToStructure<Guid>(ptr);
            return true;
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}
