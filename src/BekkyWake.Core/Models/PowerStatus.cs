namespace BekkyWake.Core.Models;

public abstract record BatteryStatus
{
    public sealed record Reading(int Percent) : BatteryStatus;
    public sealed record Unreadable(int ConsecutiveMisses) : BatteryStatus;
    public sealed record NoBatteryDesktop : BatteryStatus;

    public int? PercentOrNull => this is Reading r ? r.Percent : null;
}

public enum ThermalLevel
{
    Unavailable,
    Nominal,
    Fair,
    Serious,
    Critical
}

public sealed record PowerSnapshot(
    BatteryStatus Battery,
    bool IsCharging,
    ThermalLevel Thermal,
    double? TemperatureCelsius = null);

public sealed record RunningAppInfo(int Pid, string ProcessName, string? ExecutablePath = null, string? MainWindowTitle = null);

public sealed record AudioEndpointInfo(string DeviceId, string FriendlyName, float? Volume, bool Muted);
