using InsomniaWin.Core.Models;

namespace InsomniaWin.Core.Core;

public static class FreezePlanner
{
    private static readonly HashSet<string> HardDenied = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "dwm",
        "explorer",
        "sihost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "SearchIndexer",
        "TextInputHost",
        "Taskmgr",
        "audiodg",
        "ctfmon",
        "conhost",
        "RuntimeBroker",
        "SecurityHealthService",
        "SecurityHealthSystray",
        "MsMpEng",
        "NisSrv",
        "ApplicationFrameHost",
        "fontdrvhost",
        "WmiPrvSE",
        "spoolsv",
        "dllhost",
        "InsomniaWin",
        "Docker Desktop",
        "com.docker.backend",
        "com.docker.build",
        "vmmem",
        "vmmemWSL"
    };

    private static readonly HashSet<string> MeetingAndRecordingProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom",
        "ZoomIt",
        "ms-teams",
        "Teams",
        "CiscoCollabHost",
        "Webex",
        "webexmta",
        "obs64",
        "obs32",
        "Loom",
        "Krisp",
        "WisprFlow",
        "Granola",
        "Otter"
    };

    private static readonly HashSet<string> BuiltInProtected = new(
        MeetingAndRecordingProtected.Concat(
        [
            "WindowsTerminal",
            "pwsh",
            "powershell",
            "cmd",
            "OpenConsole",
            "Code",
            "Cursor",
            "Antigravity",
            "zed",
            "devenv",
            "chrome",
            "msedge",
            "brave",
            "arc",
            "firefox",
            "1Password",
            "Bitwarden",
            "KeePass",
            "KeePassXC",
            "tailscale-ipn",
            "tailscaled",
            "ollama",
            "ollama app",
            "LM Studio",
            "Claude",
            "ChatGPT",
            "wsl",
            "wslhost",
            "bash",
            "ssh"
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] JetBrainsPrefixes =
    [
        "rider",
        "idea",
        "pycharm",
        "goland",
        "webstorm",
        "clion",
        "datagrip",
        "rubymine",
        "phpstorm"
    ];

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    public static bool IsHardDenied(string processName)
    {
        var norm = NormalizeName(processName);
        return string.IsNullOrEmpty(norm) || HardDenied.Contains(norm);
    }

    public static bool IsBuiltInProtected(string processName)
    {
        var norm = NormalizeName(processName);
        if (string.IsNullOrEmpty(norm)) return false;
        if (BuiltInProtected.Contains(norm)) return true;
        return JetBrainsPrefixes.Any(prefix => norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<RunningAppInfo> PlanFreezeTargets(
        IEnumerable<RunningAppInfo> running,
        Config config,
        int currentPid = -1)
    {
        var runningList = running as IReadOnlyList<RunningAppInfo> ?? running.ToList();
        var selfPid = currentPid > 0 ? currentPid : Environment.ProcessId;

        var agentSet = new HashSet<string>(
            config.AgentList.Select(NormalizeName).Where(x => !string.IsNullOrEmpty(x)),
            StringComparer.OrdinalIgnoreCase);

        var freezeSet = new HashSet<string>(
            config.FreezeList.Select(NormalizeName).Where(x => !string.IsNullOrEmpty(x)),
            StringComparer.OrdinalIgnoreCase);

        // When FreezeAllApps is enabled, identify user-facing desktop app names (having a main window)
        // so we freeze all child/renderer processes of those non-protected apps without touching background OS daemons.
        var windowedNames = config.FreezeAllApps
            ? new HashSet<string>(
                runningList
                    .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    .Select(p => NormalizeName(p.ProcessName)),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var targets = new List<RunningAppInfo>();
        foreach (var proc in runningList)
        {
            if (proc.Pid <= 0 || proc.Pid == selfPid) continue;
            var norm = NormalizeName(proc.ProcessName);
            if (IsHardDenied(norm)) continue;
            if (agentSet.Contains(norm)) continue;

            if (freezeSet.Contains(norm))
            {
                targets.Add(proc);
                continue;
            }

            if (config.FreezeAllApps && windowedNames is not null && windowedNames.Contains(norm))
            {
                if (!IsBuiltInProtected(norm))
                {
                    targets.Add(proc);
                }
            }
        }

        return targets;
    }
}
