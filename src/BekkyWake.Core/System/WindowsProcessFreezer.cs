using System.Diagnostics;
using System.Management;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.Core.System;

public sealed class WindowsProcessFreezer : IProcessFreezer
{
    private string? _cachedBootSessionId;

    public string GetBootSessionId()
    {
        if (_cachedBootSessionId is not null) return _cachedBootSessionId;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["LastBootUpTime"] is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    _cachedBootSessionId = raw;
                    return _cachedBootSessionId;
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        var bootApprox = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _cachedBootSessionId = bootApprox.ToString("yyyyMMddHHmm");
        return _cachedBootSessionId;
    }

    public IReadOnlyList<RunningAppInfo> ListRunningApps()
    {
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var results = new List<RunningAppInfo>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.SessionId != currentSessionId) continue;
                string? title = null;
                try { title = proc.MainWindowTitle; } catch { /* ignore */ }
                results.Add(new RunningAppInfo(proc.Id, proc.ProcessName, null, title));
            }
            catch
            {
                // Process exited or access denied.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return results;
    }

    public ProcessIdentity? SuspendProcess(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out var creationTicks, out _, out _, out _))
            {
                return null;
            }

            var status = NativeMethods.NtSuspendProcess(handle);
            if (status != 0)
            {
                return null;
            }

            return new ProcessIdentity(creationTicks, GetBootSessionId());
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public bool IsProcessAliveMatching(FrozenProcess frozen)
    {
        if (frozen.Identity is null) return false;
        if (!string.Equals(frozen.Identity.BootSessionId, GetBootSessionId(), StringComparison.Ordinal))
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, frozen.Pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out var creationTicks, out var exitTicks, out _, out _))
            {
                return false;
            }

            return exitTicks == 0 && creationTicks == frozen.Identity.CreationTimeUtcTicks;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public bool ResumeProcess(FrozenProcess frozen)
    {
        if (frozen.Identity is null) return false;
        if (!string.Equals(frozen.Identity.BootSessionId, GetBootSessionId(), StringComparison.Ordinal))
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            frozen.Pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out var creationTicks, out _, out _, out _))
            {
                return false;
            }

            if (creationTicks != frozen.Identity.CreationTimeUtcTicks)
            {
                return false;
            }

            return NativeMethods.NtResumeProcess(handle) == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
