using System.Text;

namespace BekkyWake.Core.Store;

public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// Thread-safe rolling logger writing to Logs\bekky-wake.log (capped at 1 MiB,
/// rotating to bekky-wake.log.1). Leaves symlinks unrotated so user-managed targets work safely.
/// </summary>
public sealed class RollingLogger : IAppLogger
{
    private const long MaxBytes = 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private bool _loggedSymlinkWarning;

    public RollingLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {sanitized}{Environment.NewLine}";

        lock (_gate)
        {
            try
            {
                _paths.EnsureDirectories();
                RotateIfNeeded();
                File.AppendAllText(_paths.LogFile, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the session manager or backstop.
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_paths.LogFile))
        {
            return;
        }

        var info = new FileInfo(_paths.LogFile);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (!_loggedSymlinkWarning)
            {
                _loggedSymlinkWarning = true;
            }
            return;
        }

        if (info.Length < MaxBytes)
        {
            return;
        }

        try
        {
            File.Move(_paths.LogFile, _paths.RotatedLogFile, overwrite: true);
        }
        catch
        {
            // Best-effort rotation.
        }
    }
}
