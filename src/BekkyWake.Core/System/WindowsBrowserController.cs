using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.Core.System;

public sealed partial class WindowsBrowserController : IBrowserController
{
    private static readonly Dictionary<string, (string Id, string DisplayName)> SupportedBrowsers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = ("chrome", "Google Chrome"),
            ["msedge.exe"] = ("msedge", "Microsoft Edge"),
            ["brave.exe"] = ("brave", "Brave"),
            ["arc.exe"] = ("arc", "Arc"),
            ["chromium.exe"] = ("chromium", "Chromium")
        };

    public Task<IReadOnlyList<ThrottledBrowser>> DetectThrottledBrowsersAsync() =>
        Task.Run(DetectSync);

    private static IReadOnlyList<ThrottledBrowser> DetectSync()
    {
        var results = new List<ThrottledBrowser>();
        var seenBrowserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            const string query =
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process " +
                "WHERE Name='chrome.exe' OR Name='msedge.exe' OR Name='brave.exe' OR Name='arc.exe' OR Name='chromium.exe'";
            using var searcher = new ManagementObjectSearcher(query);

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                var cmd = obj["CommandLine"] as string;
                var exe = obj["ExecutablePath"] as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(exe))
                    continue;

                if (!SupportedBrowsers.TryGetValue(name, out var meta))
                    continue;

                // Only inspect the main browser process, not renderer/GPU/utility child processes.
                if (cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!seenBrowserIds.Add(meta.Id))
                    continue;

                var missing = new List<string>();
                if (!cmd.Contains("--disable-backgrounding-occluded-windows", StringComparison.OrdinalIgnoreCase))
                    missing.Add("--disable-backgrounding-occluded-windows");
                if (!cmd.Contains("--disable-renderer-backgrounding", StringComparison.OrdinalIgnoreCase))
                    missing.Add("--disable-renderer-backgrounding");

                if (missing.Count == 0)
                    continue;

                var pid = Convert.ToInt32(obj["ProcessId"]);
                var userDataDir = ExtractFlagValue(cmd, "user-data-dir");
                var profileDir = ExtractFlagValue(cmd, "profile-directory");

                results.Add(new ThrottledBrowser(
                    meta.Id,
                    meta.DisplayName,
                    exe,
                    pid,
                    userDataDir,
                    profileDir,
                    [.. missing]));
            }
        }
        catch
        {
            // WMI process inspection may fail under locked-down policy.
        }

        return results;
    }

    public async Task<(bool Success, string Message)> RelaunchUnthrottledAsync(ThrottledBrowser browser)
    {
        var procName = Path.GetFileNameWithoutExtension(browser.ExecutablePath);
        var running = Process.GetProcessesByName(procName);
        if (running.Length == 0)
        {
            return (false, $"{browser.DisplayName} is no longer running; nothing was launched.");
        }

        foreach (var p in running)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    p.CloseMainWindow();
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                p.Dispose();
            }
        }

        // Wait up to 10s for all processes of that browser to exit cleanly.
        var exited = false;
        for (var i = 0; i < 40; i++)
        {
            var remaining = Process.GetProcessesByName(procName);
            var count = remaining.Length;
            foreach (var p in remaining) p.Dispose();
            if (count == 0)
            {
                exited = true;
                break;
            }
            await Task.Delay(250);
        }

        if (!exited)
        {
            return (false, $"{browser.DisplayName} did not quit within 10 s; nothing new was launched to avoid duplicate profiles.");
        }

        var args = new List<string>
        {
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding"
        };

        if (!string.IsNullOrWhiteSpace(browser.UserDataDir))
        {
            args.Add($"--user-data-dir=\"{browser.UserDataDir}\"");
        }
        if (!string.IsNullOrWhiteSpace(browser.ProfileDirectory))
        {
            args.Add($"--profile-directory=\"{browser.ProfileDirectory}\"");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browser.ExecutablePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch {browser.DisplayName}: {ex.Message}");
        }

        // Verify browser appears running within 5s.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            var launched = Process.GetProcessesByName(procName);
            var count = launched.Length;
            foreach (var p in launched) p.Dispose();
            if (count > 0)
            {
                return (true, $"{browser.DisplayName} relaunched unthrottled.");
            }
        }

        return (false, $"{browser.DisplayName} was launched but did not appear running within 5 s.");
    }

    internal static string? ExtractFlagValue(string commandLine, string flagName)
    {
        var quoted = Regex.Match(commandLine, $@"--{Regex.Escape(flagName)}=""([^""]+)""", RegexOptions.IgnoreCase);
        if (quoted.Success) return quoted.Groups[1].Value;

        var unquoted = Regex.Match(commandLine, $@"--{Regex.Escape(flagName)}=([^\s""]+)", RegexOptions.IgnoreCase);
        return unquoted.Success ? unquoted.Groups[1].Value : null;
    }
}
