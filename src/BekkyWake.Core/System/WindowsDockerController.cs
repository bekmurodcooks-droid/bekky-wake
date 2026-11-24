using System.Diagnostics;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.Core.System;

public sealed class WindowsDockerController : IDockerController
{
    public bool IsDockerRunning(out RunningAppInfo? mainApp)
    {
        mainApp = null;
        foreach (var proc in Process.GetProcessesByName("Docker Desktop"))
        {
            try
            {
                if (proc.SessionId == Process.GetCurrentProcess().SessionId)
                {
                    mainApp = new RunningAppInfo(proc.Id, proc.ProcessName);
                    return true;
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                proc.Dispose();
            }
        }
        return false;
    }

    public async Task<DockerCheckStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        if (!IsDockerRunning(out _))
        {
            return DockerCheckStatus.NotRunning;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -q",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start()) return DockerCheckStatus.Error;
            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                return DockerCheckStatus.Error;
            }

            return string.IsNullOrWhiteSpace(stdout)
                ? DockerCheckStatus.Idle
                : DockerCheckStatus.Busy;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DockerCheckStatus.Error;
        }
    }

    public Task<bool> UnfreezeDockerAsync() => Task.FromResult(true);
}
