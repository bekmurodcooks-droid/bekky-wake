using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InsomniaWin.Core.Models;

namespace InsomniaWin.Core.Store;

public abstract record SessionLoadResult
{
    public sealed record Missing : SessionLoadResult;
    public sealed record Loaded(Session Session) : SessionLoadResult;
    public sealed record Unreadable(string Error) : SessionLoadResult;
}

public abstract record JournalLoadResult
{
    public sealed record Clean : JournalLoadResult;
    public sealed record Loaded(RuntimeState State) : JournalLoadResult;
    public sealed record Unreadable(string Error) : JournalLoadResult;
}

/// <summary>
/// Atomic JSON persistence for config.json, session.json, and state.json.
/// Never silently treats an unreadable state.json or session.json as clean.
/// </summary>
public sealed class JsonStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppPaths _paths;

    public JsonStore(AppPaths paths)
    {
        _paths = paths;
    }

    public Config LoadConfig(out string? warning)
    {
        warning = null;
        if (!File.Exists(_paths.ConfigFile))
        {
            return new Config();
        }

        try
        {
            var json = File.ReadAllText(_paths.ConfigFile, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<Config>(json, JsonOptions);
            if (cfg is null)
            {
                warning = "config.json decoded to null; using defaults in memory.";
                return new Config();
            }

            cfg.Presets ??= [.. Config.DefaultPresets];
            if (cfg.Presets.Count == 0) cfg.Presets = [.. Config.DefaultPresets];
            cfg.FreezeList ??= [.. Config.DefaultFreezeList];
            cfg.AgentList ??= [.. Config.DefaultAgentList];
            if (cfg.MaxDuration < 60) cfg.MaxDuration = 30 * 24 * 3600;
            if (cfg.DefaultPreset < 60) cfg.DefaultPreset = 4 * 3600;
            return cfg;
        }
        catch (Exception ex)
        {
            warning = $"Could not read config.json ({ex.Message}); using defaults without overwriting your file.";
            return new Config();
        }
    }

    public void SaveConfig(Config config) => WriteAtomic(_paths.ConfigFile, config);

    public SessionLoadResult LoadSession()
    {
        if (!File.Exists(_paths.SessionFile))
        {
            return new SessionLoadResult.Missing();
        }

        try
        {
            // Refuse symlinks/reparse points so untrusted junctions are never followed.
            var info = new FileInfo(_paths.SessionFile);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new SessionLoadResult.Unreadable("session.json is a reparse point / symlink.");
            }

            var json = File.ReadAllText(_paths.SessionFile, Encoding.UTF8);
            var session = JsonSerializer.Deserialize<Session>(json, JsonOptions);
            return session is null
                ? new SessionLoadResult.Unreadable("session.json decoded to null.")
                : new SessionLoadResult.Loaded(session);
        }
        catch (Exception ex)
        {
            return new SessionLoadResult.Unreadable(ex.Message);
        }
    }

    public void SaveSession(Session session) => WriteAtomic(_paths.SessionFile, session);

    public void DeleteSession()
    {
        if (File.Exists(_paths.SessionFile))
        {
            File.Delete(_paths.SessionFile);
        }
    }

    /// <summary>
    /// Moves corrupt or unreadable session.json aside to session.json.unreadable-<timestamp>
    /// without deleting or overwriting any existing quarantine file.
    /// </summary>
    public string? QuarantineUnreadableSession(DateTimeOffset now)
    {
        if (!File.Exists(_paths.SessionFile))
        {
            return null;
        }

        var stamp = now.ToString("yyyyMMddHHmmss");
        var candidate = $"{_paths.SessionFile}.unreadable-{stamp}";
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{_paths.SessionFile}.unreadable-{stamp}-{suffix++}";
        }

        File.Move(_paths.SessionFile, candidate, overwrite: false);
        return candidate;
    }

    public JournalLoadResult LoadState()
    {
        if (!File.Exists(_paths.StateFile))
        {
            return new JournalLoadResult.Clean();
        }

        try
        {
            var info = new FileInfo(_paths.StateFile);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new JournalLoadResult.Unreadable("state.json is a reparse point / symlink.");
            }

            var json = File.ReadAllText(_paths.StateFile, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<RuntimeState>(json, JsonOptions);
            if (state is null)
            {
                return new JournalLoadResult.Unreadable("state.json decoded to null.");
            }

            state.FrozenProcesses ??= [];
            state.SavedAudioOutputs ??= [];
            return new JournalLoadResult.Loaded(state);
        }
        catch (Exception ex)
        {
            return new JournalLoadResult.Unreadable(ex.Message);
        }
    }

    public void SaveState(RuntimeState state) => WriteAtomic(_paths.StateFile, state);

    public void WriteAtomic<T>(string targetPath, T value)
    {
        _paths.EnsureDirectories();
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, JsonOptions);
        var tmpPath = $"{targetPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
