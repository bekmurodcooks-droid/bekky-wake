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

    public bool TurnDisplayOff()
    {
        var result = NativeMethods.SendMessageTimeout(
            new IntPtr(0xFFFF), // HWND_BROADCAST
            NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER),
            new IntPtr(2), // Power off
            NativeMethods.SMTO_ABORTIFHUNG,
            1000,
            out _);
        return result != IntPtr.Zero;
    }

    public bool WakeDisplay()
    {
        var result = NativeMethods.SendMessageTimeout(
            new IntPtr(0xFFFF),
            NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER),
            new IntPtr(-1), // Power on
            NativeMethods.SMTO_ABORTIFHUNG,
            1000,
            out _);
        return result != IntPtr.Zero;
    }
}
