using System.Text.Json;
using System.Text.Json.Serialization;
using BekkyWake.Core.Core;

namespace BekkyWake.Core.Models;

public enum TrayMode
{
    Popover,
    FloatingPill,
    Minimal
}

public enum CountdownStyle
{
    Remaining,
    EndTime
}

public enum DurationField
{
    Days,
    Hours,
    Minutes
}

public enum AppRule
{
    KeepRunning,
    Freeze
}

/// <summary>
/// Resilient enum converter that falls back to the default enum value instead
/// of failing the entire config deserialization when an unknown string appears.
/// </summary>
public sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private readonly TEnum _fallback;

    public TolerantEnumConverter(TEnum fallback) => _fallback = fallback;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                return parsed;
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var num))
        {
            var val = (TEnum)Enum.ToObject(typeof(TEnum), num);
            if (Enum.IsDefined(val))
            {
                return val;
            }
        }
        else
        {
            reader.Skip();
        }

        return _fallback;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

public sealed class TrayModeConverter() : JsonConverter<TrayMode>
{
    private static readonly TolerantEnumConverter<TrayMode> Inner = new(TrayMode.Popover);
    public override TrayMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Inner.Read(ref reader, typeToConvert, options);
    public override void Write(Utf8JsonWriter writer, TrayMode value, JsonSerializerOptions options) => Inner.Write(writer, value, options);
}

public sealed class CountdownStyleConverter() : JsonConverter<CountdownStyle>
{
    private static readonly TolerantEnumConverter<CountdownStyle> Inner = new(CountdownStyle.Remaining);
    public override CountdownStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Inner.Read(ref reader, typeToConvert, options);
    public override void Write(Utf8JsonWriter writer, CountdownStyle value, JsonSerializerOptions options) => Inner.Write(writer, value, options);
}

public sealed class DurationFieldConverter() : JsonConverter<DurationField>
{
    private static readonly TolerantEnumConverter<DurationField> Inner = new(DurationField.Hours);
    public override DurationField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Inner.Read(ref reader, typeToConvert, options);
    public override void Write(Utf8JsonWriter writer, DurationField value, JsonSerializerOptions options) => Inner.Write(writer, value, options);
}

/// <summary>
/// User settings persisted as config.json. Missing or unknown fields gracefully
/// resolve to safe defaults so upgrades or hand-edits never lose settings.
/// </summary>
public sealed class Config
{
    public const int FloorStep = 5;
    public const int MaxEndFloor = 100 - FloorStep;

    public static readonly double[] DefaultPresets =
    [
        30 * 60,
        1 * 3600,
        2 * 3600,
        4 * 3600,
        8 * 3600,
        12 * 3600,
        24 * 3600,
        3 * 24 * 3600
    ];

    public static readonly string[] DefaultFreezeList =
    [
        "slack",
        "WhatsApp",
        "Discord"
    ];

    public static readonly string[] DefaultAgentList =
    [
        "WindowsTerminal",
        "pwsh",
        "powershell",
        "cmd",
        "Code",
        "Cursor",
        "Antigravity",
        "zed",
        "rider64",
        "idea64",
        "pycharm64",
        "devenv",
        "chrome",
        "msedge",
        "brave",
        "arc",
        "firefox",
        "Docker Desktop",
        "Claude",
        "ChatGPT",
        "LM Studio",
        "ollama",
        "ollama app",
        "tailscale-ipn",
        "wsl",
        "wslhost"
    ];

    // UI
    [JsonConverter(typeof(TrayModeConverter))]
    public TrayMode TrayMode { get; set; } = TrayMode.Popover;
    public bool OneClickStart { get; set; } = false;
    public bool ShowFloatingCountdown { get; set; } = true;

    [JsonConverter(typeof(CountdownStyleConverter))]
    public CountdownStyle CountdownStyle { get; set; } = CountdownStyle.Remaining;
    public bool SessionSounds { get; set; } = false;
    public bool LaunchAtLogin { get; set; } = false;
    public double? FloatingPillLeft { get; set; }
    public double? FloatingPillTop { get; set; }

    // Session
    public List<double> Presets { get; set; } = [.. DefaultPresets];
    public double DefaultPreset { get; set; } = 4 * 3600;

    [JsonConverter(typeof(DurationFieldConverter))]
    public DurationField DefaultFocusField { get; set; } = DurationField.Hours;
    public double MaxDuration { get; set; } = 30 * 24 * 3600;
    public bool KeepScreenAwake { get; set; } = true;

    // Lid-close actions
    public List<string> FreezeList { get; set; } = [.. DefaultFreezeList];
    public List<string> AgentList { get; set; } = [.. DefaultAgentList];
    public bool FreezeAllApps { get; set; } = false;
    public bool DockerRule { get; set; } = false;
    public bool MuteOnLidClose { get; set; } = true;
    public bool DarkenDisplayOnLidClose { get; set; } = true;
    public bool LockScreenOnLidClose { get; set; } = false;
    public bool LowPowerOnLidClose { get; set; } = false;

    // Battery / thermal floors
    public int LowPowerFloor { get; set; } = 40;
    public int EndFloor { get; set; } = 10;
    public bool ThermalRules { get; set; } = true;
    public bool ThermalHoldUntilNominal { get; set; } = true;

    [JsonIgnore]
    public bool FloorsAreOrdered => EndFloor == 0 || EndFloor < LowPowerFloor;

    [JsonIgnore]
    public FloorInputs CurrentFloorInputs => new(
        LowPowerFloor,
        EndFloor,
        ThermalRules,
        ThermalHoldUntilNominal,
        LowPowerOnLidClose);

    public void SetEndFloor(int value)
    {
        EndFloor = Math.Clamp(value, 0, MaxEndFloor);
        if (EndFloor > 0 && LowPowerFloor <= EndFloor)
        {
            LowPowerFloor = Math.Min(EndFloor + FloorStep, 100);
        }
    }

    public void SetLowPowerFloor(int value)
    {
        LowPowerFloor = Math.Clamp(value, 0, 100);
        if (EndFloor > 0 && EndFloor >= LowPowerFloor)
        {
            EndFloor = Math.Max(LowPowerFloor - FloorStep, 0);
        }
    }

    /// <summary>
    /// Clamps both floors to valid bounds and ensures EndFloor stays strictly below
    /// LowPowerFloor when non-zero by raising LowPowerFloor.
    /// </summary>
    public string? NormalizeFloors()
    {
        var beforeLow = LowPowerFloor;
        var beforeEnd = EndFloor;

        LowPowerFloor = Math.Clamp(LowPowerFloor, 0, 100);
        EndFloor = Math.Clamp(EndFloor, 0, MaxEndFloor);
        if (!FloorsAreOrdered)
        {
            LowPowerFloor = Math.Min(EndFloor + FloorStep, 100);
        }

        if (LowPowerFloor == beforeLow && EndFloor == beforeEnd)
        {
            return null;
        }

        return $"battery floors corrected: lowPowerFloor {beforeLow} -> {LowPowerFloor}, endFloor {beforeEnd} -> {EndFloor}";
    }

    public static IReadOnlyList<int> FloorChoices(int max, int keeping)
    {
        var set = new SortedSet<int>();
        for (var i = 0; i <= max; i += FloorStep)
        {
            set.Add(i);
        }
        set.Add(Math.Clamp(keeping, 0, 100));
        return [.. set];
    }

    [JsonIgnore]
    public IReadOnlyList<string> ListedApps
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var raw in AgentList.Concat(FreezeList))
            {
                var norm = FreezePlanner.NormalizeName(raw);
                if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                {
                    list.Add(norm);
                }
            }
            return list;
        }
    }

    public IReadOnlyList<string> AppsWithRule(AppRule rule) =>
        ListedApps.Where(name => RuleFor(name) == rule).ToList();

    public AppRule? RuleFor(string processName)
    {
        var norm = FreezePlanner.NormalizeName(processName);
        if (string.IsNullOrEmpty(norm)) return null;

        var onAgent = AgentList.Any(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase));
        var onFreeze = FreezeList.Any(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase));

        if (!onAgent && !onFreeze) return null;
        if (FreezePlanner.IsHardDenied(norm) || onAgent) return AppRule.KeepRunning;
        return AppRule.Freeze;
    }

    public void SetRule(AppRule? rule, string processName)
    {
        var norm = FreezePlanner.NormalizeName(processName);
        if (string.IsNullOrEmpty(norm)) return;

        if (rule != AppRule.KeepRunning)
        {
            AgentList.RemoveAll(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase));
        }
        if (rule != AppRule.Freeze)
        {
            FreezeList.RemoveAll(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase));
        }

        if (rule == AppRule.KeepRunning && !AgentList.Any(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase)))
        {
            AgentList.Add(norm);
        }
        else if (rule == AppRule.Freeze && !FreezeList.Any(x => string.Equals(FreezePlanner.NormalizeName(x), norm, StringComparison.OrdinalIgnoreCase)))
        {
            FreezeList.Add(norm);
        }
    }

    public void AddApp(string processName)
    {
        var norm = FreezePlanner.NormalizeName(processName);
        if (string.IsNullOrEmpty(norm)) return;
        if (RuleFor(norm) is null)
        {
            SetRule(AppRule.KeepRunning, norm);
        }
    }

    public Config Clone()
    {
        return new Config
        {
            TrayMode = TrayMode,
            OneClickStart = OneClickStart,
            ShowFloatingCountdown = ShowFloatingCountdown,
            CountdownStyle = CountdownStyle,
            SessionSounds = SessionSounds,
            LaunchAtLogin = LaunchAtLogin,
            FloatingPillLeft = FloatingPillLeft,
            FloatingPillTop = FloatingPillTop,
            Presets = [.. Presets],
            DefaultPreset = DefaultPreset,
            DefaultFocusField = DefaultFocusField,
            MaxDuration = MaxDuration,
            KeepScreenAwake = KeepScreenAwake,
            FreezeList = [.. FreezeList],
            AgentList = [.. AgentList],
            FreezeAllApps = FreezeAllApps,
            DockerRule = DockerRule,
            MuteOnLidClose = MuteOnLidClose,
            DarkenDisplayOnLidClose = DarkenDisplayOnLidClose,
            LockScreenOnLidClose = LockScreenOnLidClose,
            LowPowerOnLidClose = LowPowerOnLidClose,
            LowPowerFloor = LowPowerFloor,
            EndFloor = EndFloor,
            ThermalRules = ThermalRules,
            ThermalHoldUntilNominal = ThermalHoldUntilNominal
        };
    }

    public sealed record FloorInputs(
        int LowPowerFloor,
        int EndFloor,
        bool ThermalRules,
        bool ThermalHoldUntilNominal,
        bool LowPowerOnLidClose);
}
