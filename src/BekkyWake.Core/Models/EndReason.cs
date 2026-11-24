namespace BekkyWake.Core.Models;

public enum EndReason
{
    User,
    Timer,
    BatteryFloor,
    BatteryUnreadable,
    ThermalCritical,
    BackstopRecovery,
    Quit
}

public static class EndReasonExtensions
{
    public static bool ShouldSleepIfLidClosed(this EndReason reason) =>
        reason is EndReason.Timer
            or EndReason.BatteryFloor
            or EndReason.BatteryUnreadable
            or EndReason.ThermalCritical;

    public static string Description(this EndReason reason) => reason switch
    {
        EndReason.User => "Ended by user",
        EndReason.Timer => "Timer finished",
        EndReason.BatteryFloor => "Battery reached cutoff floor",
        EndReason.BatteryUnreadable => "Battery unreadable while discharging",
        EndReason.ThermalCritical => "Critical temperature reached",
        EndReason.BackstopRecovery => "Recovered by backstop",
        EndReason.Quit => "App quit",
        _ => reason.ToString()
    };
}
