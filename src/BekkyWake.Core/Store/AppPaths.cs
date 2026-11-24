using System.Security.Cryptography;
using System.Text;

namespace BekkyWake.Core.Store;

public sealed class AppPaths
{
    public const string HomeEnvVar = "BEKKY_WAKE_HOME";

    public string RootDir { get; }
    public string ConfigFile { get; }
    public string SessionFile { get; }
    public string StateFile { get; }
    public string RecoveryLockFile { get; }
    public string LogsDir { get; }
    public string LogFile { get; }
    public string RotatedLogFile { get; }
    public string SimulateLidFile { get; }
    public string PipeName { get; }
    public string SingleInstanceMutexName { get; }

    public AppPaths(string? rootDir = null)
    {
        var resolved = string.IsNullOrWhiteSpace(rootDir)
            ? ResolveDefaultRoot()
            : Path.GetFullPath(rootDir);

        RootDir = resolved;
        ConfigFile = Path.Combine(RootDir, "config.json");
        SessionFile = Path.Combine(RootDir, "session.json");
        StateFile = Path.Combine(RootDir, "state.json");
        RecoveryLockFile = Path.Combine(RootDir, "recovery.lock");
        LogsDir = Path.Combine(RootDir, "Logs");
        LogFile = Path.Combine(LogsDir, "bekky-wake.log");
        RotatedLogFile = Path.Combine(LogsDir, "bekky-wake.log.1");
        SimulateLidFile = Path.Combine(RootDir, "simulate-lid.trigger");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(RootDir.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes)[..16];
        PipeName = $"BekkyWake_Session_{hash}";
        SingleInstanceMutexName = $"Local\\BekkyWake_Gui_{hash}";
    }

    public static AppPaths FromEnvironment()
    {
        var envHome = Environment.GetEnvironmentVariable(HomeEnvVar);
        return new AppPaths(string.IsNullOrWhiteSpace(envHome) ? null : envHome);
    }

    private static string ResolveDefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "bekky-wake");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogsDir);
    }
}
