using System.Management;
using InsomniaWin.Core.Core;
using InsomniaWin.Core.Models;

namespace InsomniaWin.Core.System;

public sealed class WindowsPowerMonitor : IPowerMonitor
{
    public PowerSnapshot Sample()
    {
        var (battery, charging) = SampleBattery();
        var (thermal, tempC) = SampleThermal();
        return new PowerSnapshot(battery, charging, thermal, tempC);
    }

    private static (BatteryStatus Battery, bool IsCharging) SampleBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return (new BatteryStatus.Unreadable(1), false);
        }

        var isCharging = status.ACLineStatus == 1 || (status.BatteryFlag & 8) != 0;
        if (status.BatteryFlag == 128)
        {
            return (new BatteryStatus.NoBatteryDesktop(), true);
        }

        if (status.BatteryLifePercent == 255)
        {
            return (new BatteryStatus.Unreadable(1), isCharging);
        }

        var pct = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        return (new BatteryStatus.Reading(pct), isCharging);
    }

    private static (ThermalLevel Level, double? MaxCelsius) SampleThermal()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            double? maxC = null;

            foreach (ManagementObject obj in searcher.Get())
            {
                double? celsius = null;
                if (obj["HighPrecisionTemperature"] is uint hp && hp > 1000 && hp < 4500)
                {
                    celsius = hp / 10.0 - 273.15;
                }
                else if (obj["Temperature"] is uint t && t > 100 && t < 450)
                {
                    celsius = t - 273.15;
                }

                if (celsius.HasValue && (!maxC.HasValue || celsius.Value > maxC.Value))
                {
                    maxC = celsius.Value;
                }
            }

            if (!maxC.HasValue)
            {
                return (ThermalLevel.Unavailable, null);
            }

            var c = maxC.Value;
            var level = c switch
            {
                >= 92.0 => ThermalLevel.Critical,
                >= 82.0 => ThermalLevel.Serious,
                >= 72.0 => ThermalLevel.Fair,
                _ => ThermalLevel.Nominal
            };
            return (level, Math.Round(c, 1));
        }
        catch
        {
            return (ThermalLevel.Unavailable, null);
        }
    }
}
