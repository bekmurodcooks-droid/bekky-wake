using System.Runtime.InteropServices;
using BekkyWake.Core.Core;
using BekkyWake.Core.Models;

namespace BekkyWake.Core.System;

public sealed class WindowsAudioController : IAudioController
{
    private const int DataFlowRender = 0; // eRender
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsCtxAll = 23;
    private const uint StgmRead = 0;
    private const ushort VtLpWstr = 31;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public IReadOnlyList<AudioEndpointInfo> GetActiveRenderEndpoints()
    {
        var list = new List<AudioEndpointInfo>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null) return list;
            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator is null) return list;

            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out collection) != 0 || collection is null)
                return list;

            if (collection.GetCount(out var count) != 0)
                return list;

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                IAudioEndpointVolume? vol = null;
                try
                {
                    if (collection.Item(i, out dev) != 0 || dev is null) continue;
                    if (dev.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) continue;

                    var name = ReadFriendlyName(dev) ?? id;

                    var iid = IidIAudioEndpointVolume;
                    if (dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volObj) != 0 || volObj is not IAudioEndpointVolume endpointVol)
                        continue;

                    vol = endpointVol;
                    float? scalar = vol.GetMasterVolumeLevelScalar(out var level) == 0 ? level : null;
                    var muted = vol.GetMute(out var isMuted) == 0 && isMuted;
                    list.Add(new AudioEndpointInfo(id, name, scalar, muted));
                }
                catch
                {
                    // Skip broken endpoint cleanly.
                }
                finally
                {
                    if (vol is not null) Marshal.FinalReleaseComObject(vol);
                    if (dev is not null) Marshal.FinalReleaseComObject(dev);
                }
            }
        }
        catch
        {
            // No audio service or headless environment.
        }
        finally
        {
            if (collection is not null) Marshal.FinalReleaseComObject(collection);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }

        return list;
    }

    public IReadOnlySet<string> GetConnectedRenderDeviceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null) return ids;
            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator is null) return ids;

            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out collection) != 0 || collection is null)
                return ids;

            if (collection.GetCount(out var count) != 0) return ids;

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                try
                {
                    if (collection.Item(i, out dev) == 0 && dev is not null && dev.GetId(out var id) == 0 && !string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
                finally
                {
                    if (dev is not null) Marshal.FinalReleaseComObject(dev);
                }
            }
        }
        catch
        {
            // Ignore when audio service unavailable.
        }
        finally
        {
            if (collection is not null) Marshal.FinalReleaseComObject(collection);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }

        return ids;
    }

    public bool MuteEndpoint(string deviceId) =>
        WithEndpointVolume(deviceId, vol =>
        {
            var ctx = Guid.Empty;
            return vol.SetMute(true, ref ctx) >= 0;
        });

    public bool RestoreEndpoint(SavedAudioOutput saved) =>
        WithEndpointVolume(saved.DeviceId, vol =>
        {
            var ctx = Guid.Empty;
            var ok = true;
            if (saved.Volume is float scalar)
            {
                ok &= vol.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0f, 1f), ref ctx) >= 0;
            }
            ok &= vol.SetMute(saved.Muted, ref ctx) >= 0;
            return ok;
        });

    private static bool WithEndpointVolume(string deviceId, Func<IAudioEndpointVolume, bool> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? dev = null;
        IAudioEndpointVolume? vol = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null) return false;
            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator is null) return false;

            if (enumerator.GetDevice(deviceId, out dev) != 0 || dev is null) return false;
            if (dev.GetState(out var state) != 0 || state != DeviceStateActive) return false;

            var iid = IidIAudioEndpointVolume;
            if (dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volObj) != 0 || volObj is not IAudioEndpointVolume endpointVol)
                return false;

            vol = endpointVol;
            return action(vol);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (vol is not null) Marshal.FinalReleaseComObject(vol);
            if (dev is not null) Marshal.FinalReleaseComObject(dev);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static string? ReadFriendlyName(IMMDevice dev)
    {
        IPropertyStore? store = null;
        var pv = default(PropVariant);
        try
        {
            if (dev.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;
            var key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out pv) != 0) return null;
            if (pv.vt == VtLpWstr && pv.pwszVal != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(pv.pwszVal);
            }
            return null;
        }
        finally
        {
            PropVariantClear(ref pv);
            if (store is not null) Marshal.FinalReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
