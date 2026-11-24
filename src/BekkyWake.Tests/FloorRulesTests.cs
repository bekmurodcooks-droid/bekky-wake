using BekkyWake.Core.Core;
using BekkyWake.Core.Models;
using Xunit;

namespace BekkyWake.Tests;

public sealed class FloorRulesTests
{
    [Fact]
    public void Evaluate_EndsOnBatteryBelowEndFloorOnlyWhenDischarging()
    {
        var cfg = new Config { LowPowerFloor = 40, EndFloor = 10 };

        var discharging = FloorRules.Evaluate(new BatteryStatus.Reading(9), isCharging: false, ThermalLevel.Nominal, false, false, false, cfg);
        Assert.Single(discharging);
        Assert.Equal(new FloorAction.EndSession(EndReason.BatteryFloor), discharging[0]);

        var charging = FloorRules.Evaluate(new BatteryStatus.Reading(9), isCharging: true, ThermalLevel.Nominal, false, false, false, cfg);
        Assert.Empty(charging);

        // EndFloor = 0 disables battery cutoff.
        cfg.EndFloor = 0;
        var disabled = FloorRules.Evaluate(new BatteryStatus.Reading(2), isCharging: false, ThermalLevel.Nominal, false, false, false, cfg);
        Assert.Single(disabled);
        Assert.Equal(new FloorAction.EnableLowPower(LowPowerCause.Battery), disabled[0]);
    }

    [Fact]
    public void Evaluate_EndsAfterTwoConsecutiveUnreadableBatteryReads()
    {
        var cfg = new Config { EndFloor = 10 };
        var oneMiss = FloorRules.Evaluate(new BatteryStatus.Unreadable(1), false, ThermalLevel.Nominal, false, false, false, cfg);
        Assert.Empty(oneMiss);

        var twoMisses = FloorRules.Evaluate(new BatteryStatus.Unreadable(2), false, ThermalLevel.Nominal, false, false, false, cfg);
        Assert.Single(twoMisses);
        Assert.Equal(new FloorAction.EndSession(EndReason.BatteryUnreadable), twoMisses[0]);
    }

    [Fact]
    public void Evaluate_ThermalCriticalEndsAndSeriousHoldsUntilNominal()
    {
        var cfg = new Config { ThermalRules = true, ThermalHoldUntilNominal = true };

        var critical = FloorRules.Evaluate(new BatteryStatus.NoBatteryDesktop(), true, ThermalLevel.Critical, true, false, false, cfg);
        Assert.Equal(new FloorAction.EndSession(EndReason.ThermalCritical), Assert.Single(critical));

        // At Fair while still hot -> remains enabled (no DisableLowPower).
        var holdAtFair = FloorRules.Evaluate(new BatteryStatus.NoBatteryDesktop(), true, ThermalLevel.Fair, wasHot: true, lidClosed: false, lowPowerSetByUs: true, cfg);
        Assert.Empty(holdAtFair);

        // With hold disabled -> Fair drops Low Power Mode.
        cfg.ThermalHoldUntilNominal = false;
        var dropAtFair = FloorRules.Evaluate(new BatteryStatus.NoBatteryDesktop(), true, ThermalLevel.Fair, wasHot: true, lidClosed: false, lowPowerSetByUs: true, cfg);
        Assert.Equal(new FloorAction.DisableLowPower(), Assert.Single(dropAtFair));
    }

    [Fact]
    public void Evaluate_PriorityOrder_ThermalBeatsBatteryBeatsLid()
    {
        var cfg = new Config { LowPowerFloor = 40, EndFloor = 10, LowPowerOnLidClose = true, ThermalRules = true };

        var thermalFirst = FloorRules.Evaluate(new BatteryStatus.Reading(25), false, ThermalLevel.Serious, true, true, false, cfg);
        Assert.Equal(new FloorAction.EnableLowPower(LowPowerCause.Thermal), Assert.Single(thermalFirst));

        var batteryNext = FloorRules.Evaluate(new BatteryStatus.Reading(25), false, ThermalLevel.Nominal, false, true, false, cfg);
        Assert.Equal(new FloorAction.EnableLowPower(LowPowerCause.Battery), Assert.Single(batteryNext));

        var lidNext = FloorRules.Evaluate(new BatteryStatus.Reading(90), true, ThermalLevel.Nominal, false, true, false, cfg);
        Assert.Equal(new FloorAction.EnableLowPower(LowPowerCause.Lid), Assert.Single(lidNext));
    }
}
