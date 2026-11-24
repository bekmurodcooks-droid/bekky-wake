using InsomniaWin.Core.Models;

namespace InsomniaWin.Core.Core;

public enum LowPowerCause
{
    Thermal,
    Battery,
    Lid
}

public abstract record FloorAction
{
    public sealed record EnableLowPower(LowPowerCause Cause) : FloorAction;
    public sealed record DisableLowPower : FloorAction;
    public sealed record EndSession(EndReason Reason) : FloorAction;
}

public static class FloorRules
{
    /// <summary>
    /// Evaluates battery, thermal, and lid state against Config floors.
    /// Priority: EndSession(BatteryFloor) -> EndSession(ThermalCritical) -> EndSession(BatteryUnreadable)
    /// -> Enable/Disable Low Power Mode (Thermal > Battery > Lid).
    /// </summary>
    public static IReadOnlyList<FloorAction> Evaluate(
        BatteryStatus battery,
        bool isCharging,
        ThermalLevel thermal,
        bool wasHot,
        bool lidClosed,
        bool lowPowerSetByUs,
        Config config)
    {
        var onBattery = !isCharging;
        var percent = battery.PercentOrNull;

        if (percent is int p && onBattery && p < config.EndFloor)
        {
            return [new FloorAction.EndSession(EndReason.BatteryFloor)];
        }

        if (config.ThermalRules && thermal == ThermalLevel.Critical)
        {
            return [new FloorAction.EndSession(EndReason.ThermalCritical)];
        }

        if (battery is BatteryStatus.Unreadable u && onBattery && u.ConsecutiveMisses >= 2 && config.EndFloor > 0)
        {
            return [new FloorAction.EndSession(EndReason.BatteryUnreadable)];
        }

        var cause = DetermineLowPowerCause(percent, isCharging, thermal, wasHot, lidClosed, config);
        if (cause is LowPowerCause c && !lowPowerSetByUs)
        {
            return [new FloorAction.EnableLowPower(c)];
        }

        if (cause is null && lowPowerSetByUs)
        {
            return [new FloorAction.DisableLowPower()];
        }

        return [];
    }

    public static LowPowerCause? DetermineLowPowerCause(
        int? percent,
        bool isCharging,
        ThermalLevel thermal,
        bool wasHot,
        bool lidClosed,
        Config config)
    {
        if (config.ThermalRules
            && (thermal == ThermalLevel.Serious || (config.ThermalHoldUntilNominal && wasHot && thermal == ThermalLevel.Fair)))
        {
            return LowPowerCause.Thermal;
        }

        if (percent is int p && !isCharging && p < config.LowPowerFloor)
        {
            return LowPowerCause.Battery;
        }

        if (lidClosed && config.LowPowerOnLidClose)
        {
            return LowPowerCause.Lid;
        }

        return null;
    }
}
