using System.Runtime.InteropServices;
using System.Windows.Interop;
using BekkyWake.Core.Core;
using BekkyWake.Core.Store;

namespace BekkyWake.Core.System;

public sealed class WindowsLidObserver : ILidObserver
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _log;
    private HwndSource? _hwndSource;
    private IntPtr _powerNotifyHandle;
    private FileSystemWatcher? _simWatcher;
    private bool _started;
    private bool _disposed;

    public bool IsLidClosed { get; private set; }
    public bool HasExternalDisplay => CheckHasExternalDisplay();

    public bool IsSimulationBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return Environment.GetEnvironmentVariable("BEKKY_WAKE_LID_SIMULATION") == "1";
#endif
        }
    }

    public event Action<bool>? LidStateChanged;
    public event Action? PowerStatusChanged;
    public event Action? DisplayTopologyChanged;

    public WindowsLidObserver(AppPaths paths, IAppLogger log)
    {
        _paths = paths;
        _log = log;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        var parameters = new HwndSourceParameters("BekkyWake_PowerObserver")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        var lidGuid = NativeMethods.GUID_LIDSWITCH_STATE_CHANGE;
        _powerNotifyHandle = NativeMethods.RegisterPowerSettingNotification(
            _hwndSource.Handle,
            ref lidGuid,
            NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);

        if (_powerNotifyHandle == IntPtr.Zero)
        {
            _log.Warn($"RegisterPowerSettingNotification failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        if (IsSimulationBuild)
        {
            StartSimulationWatcher();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_POWERBROADCAST)
        {
            var code = wParam.ToInt32();
            if (code == NativeMethods.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<NativeMethods.POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == NativeMethods.GUID_LIDSWITCH_STATE_CHANGE && setting.DataLength >= 1)
                {
                    var closed = setting.Data == 0;
                    if (closed != IsLidClosed)
                    {
                        IsLidClosed = closed;
                        LidStateChanged?.Invoke(closed);
                    }
                }
            }
            else if (code == NativeMethods.PBT_APMPOWERSTATUSCHANGE)
            {
                PowerStatusChanged?.Invoke();
            }
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            DisplayTopologyChanged?.Invoke();
        }

        return IntPtr.Zero;
    }

    private void StartSimulationWatcher()
    {
        try
        {
            _paths.EnsureDirectories();
            _simWatcher = new FileSystemWatcher(_paths.RootDir, Path.GetFileName(_paths.SimulateLidFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            _simWatcher.Changed += (_, _) => HandleSimulateLidFile();
            _simWatcher.Created += (_, _) => HandleSimulateLidFile();
            _simWatcher.EnableRaisingEvents = true;
            _log.Info("Lid simulation watcher enabled.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to start simulate-lid watcher: {ex.Message}");
        }
    }

    private void HandleSimulateLidFile()
    {
        try
        {
            Thread.Sleep(50);
            if (!File.Exists(_paths.SimulateLidFile)) return;
            var text = File.ReadAllText(_paths.SimulateLidFile).Trim().ToLowerInvariant();
            if (text == "closed")
            {
                IsLidClosed = true;
                LidStateChanged?.Invoke(true);
            }
            else if (text == "open")
            {
                IsLidClosed = false;
                LidStateChanged?.Invoke(false);
            }
        }
        catch
        {
            // Ignore transient file read locks.
        }
    }

    private static bool CheckHasExternalDisplay()
    {
        try
        {
            var rc = NativeMethods.GetDisplayConfigBufferSizes(
                NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                out var pathCount,
                out var modeCount);
            if (rc == 0 && pathCount > 0)
            {
                var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
                rc = NativeMethods.QueryDisplayConfig(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);

                if (rc == 0)
                {
                    for (var i = 0; i < pathCount; i++)
                    {
                        var tech = paths[i].targetInfo.outputTechnology;
                        var isInternal = tech is NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                            or NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                            or NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
                            or NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
                        if (!isInternal)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
        }
        catch
        {
            // Fall back to Screen count below.
        }

        return global::System.Windows.Forms.Screen.AllScreens.Length > 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _simWatcher?.Dispose();
        if (_powerNotifyHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerSettingNotification(_powerNotifyHandle);
            _powerNotifyHandle = IntPtr.Zero;
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
