using System;
using System.Runtime.InteropServices;

namespace GlassDock.Interop;

/// <summary>Minimal Core Audio wrapper for the default output device's master volume.</summary>
internal static class Audio
{
    private static IAudioEndpointVolume? GetEndpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IMMDevice device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object o);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
            return (IAudioEndpointVolume)o;
        }
        catch { return null; }
    }

    /// <summary>Returns (level 0..1, muted), or null if no output device.</summary>
    public static (float Level, bool Muted)? Get()
    {
        var ep = GetEndpoint();
        if (ep == null) return null;
        try
        {
            ep.GetMasterVolumeLevelScalar(out float level);
            ep.GetMute(out bool muted);
            return (level, muted);
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(ep); }
    }

    public static void Adjust(float delta)
    {
        var ep = GetEndpoint();
        if (ep == null) return;
        try
        {
            ep.GetMasterVolumeLevelScalar(out float level);
            float next = Math.Clamp(level + delta, 0f, 1f);
            var ctx = Guid.Empty;
            ep.SetMasterVolumeLevelScalar(next, ref ctx);
            if (next > 0) ep.SetMute(false, ref ctx);
        }
        catch { }
        finally { Marshal.ReleaseComObject(ep); }
    }

    public static void ToggleMute()
    {
        var ep = GetEndpoint();
        if (ep == null) return;
        try
        {
            ep.GetMute(out bool muted);
            var ctx = Guid.Empty;
            ep.SetMute(!muted, ref ctx);
        }
        catch { }
        finally { Marshal.ReleaseComObject(ep); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
