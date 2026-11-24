using System.Management;
using BekkyWake.Core.Core;

namespace BekkyWake.Core.System;

public sealed class WindowsDisplayController : IDisplayController
{
    public byte? ReadBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentBrightness"] is byte b)
                {
                    return b;
                }
            }
        }
        catch
        {
            // Desktop or external display without DDC/WMI brightness support.
        }
        return null;
    }

    public bool SetBrightness(byte level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            var any = false;
            foreach (ManagementObject obj in searcher.Get())
            {
                obj.InvokeMethod("WmiSetBrightness", [1u, level]);
                any = true;
            }
            return any;
        }
        catch
        {
            return false;
        }
    }
}
