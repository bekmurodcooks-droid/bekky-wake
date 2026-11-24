using System.Diagnostics;
using InsomniaWin.Core.Core;
using InsomniaWin.Core.Store;
using Microsoft.Win32;

namespace InsomniaWin.Core.System;

public sealed class WindowsBackstopScheduler : IBackstopScheduler
{
    public const string TaskName = "InsomniaWinBackstop";
    private readonly IAppLogger _log;

    public WindowsBackstopScheduler(IAppLogger log)
    {
        _log = log;
    }

    public bool IsRegistered()
    {
        var (exit, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    public bool EnsureRegistered(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        var quotedExe = $"\\\"{executablePath}\\\" --backstop";
        var args = $"/Create /F /SC MINUTE /MO 1 /TN \"{TaskName}\" /TR \"{quotedExe}\"";
        var (exit, output) = RunSchtasks(args);
        if (exit != 0)
        {
            _log.Warn($"schtasks /Create failed (exit={exit}): {output}");
            return false;
        }
        return true;
    }

    public bool RemoveRegistration()
    {
        var (exit, output) = RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        if (exit != 0 && !output.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"schtasks /Delete failed (exit={exit}): {output}");
            return false;
        }
        return true;
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!proc.Start()) return (-1, "Could not start schtasks.exe");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout} {stderr}".Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "InsomniaWin";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled, string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{executablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class WindowsScreenLocker : IScreenLocker
{
    public bool Lock() => NativeMethods.LockWorkStation();
}

public sealed class NullNotifier : INotifier
{
    public void Post(string title, string body, bool isWarning = false) { }
}
