namespace InsomniaWin.Core.Models;

public sealed record ThrottledBrowser(
    string BrowserId,
    string DisplayName,
    string ExecutablePath,
    int Pid,
    string? UserDataDir,
    string? ProfileDirectory,
    string[] MissingFlags);
