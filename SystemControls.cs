using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GlassDock.Interop;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace GlassDock;

/// <summary>Reads and changes the system state behind the Quick Settings panel's tiles and sliders.</summary>
internal static class SystemControls
{
    // ---------------- Wi-Fi / Bluetooth radios ----------------

    private static bool _radioAccess;

    public static async Task<Radio?> GetRadioAsync(RadioKind kind)
    {
        try
        {
            if (!_radioAccess) _radioAccess = await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    public static async Task SetRadioAsync(Radio radio, bool on)
    {
        try { await radio.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Name of the Wi-Fi network in use, if connected over Wi-Fi.</summary>
    public static string? WifiNetworkName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is { IsWlanConnectionProfile: true }) return profile.WlanConnectionProfileDetails.GetConnectedSsid();
        }
        catch { }
        return null;
    }

    /// <summary>Name of a connected Bluetooth device (classic or LE), if any.</summary>
    public static async Task<string?> ConnectedBluetoothDeviceAsync()
    {
        try
        {
            foreach (var selector in new[]
            {
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            })
            {
                var devices = await DeviceInformation.FindAllAsync(selector);
                var named = devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Name));
                if (named != null) return named.Name;
            }
        }
        catch { }
        return null;
    }

    // ---------------- airplane mode ----------------
    // There's no public API; this is the radio manager Windows' own Quick Settings uses. If it isn't available,
    // the tile opens the airplane mode settings page instead.

    /// <summary>True/false, or null if the state can't be read here.</summary>
    public static bool? IsAirplaneModeOn()
    {
        try
        {
            var rm = (IRadioManager)Activator.CreateInstance(Type.GetTypeFromCLSID(RadioManagerClsid)!)!;
            try
            {
                if (rm.GetSystemRadioState(out int enabled, out _, out _) != 0) return null;
                return enabled == 0;
            }
            finally { Marshal.ReleaseComObject(rm); }
        }
        catch { return null; }
    }

    /// <summary>Returns false if airplane mode couldn't be changed here.</summary>
    public static bool SetAirplaneMode(bool on)
    {
        try
        {
            var rm = (IRadioManager)Activator.CreateInstance(Type.GetTypeFromCLSID(RadioManagerClsid)!)!;
            try { return rm.SetSystemRadioState(on ? 0 : 1) == 0; }
            finally { Marshal.ReleaseComObject(rm); }
        }
        catch { return false; }
    }

    private static readonly Guid RadioManagerClsid = new("581333F6-28DB-41BE-BC7A-FF201F12F3F6");

    [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRadioManager
    {
        [PreserveSig] int IsRMSupported(out uint state);
        [PreserveSig] int GetUIRadioInstances([MarshalAs(UnmanagedType.IUnknown)] out object instances);
        [PreserveSig] int GetSystemRadioState(out int enabled, out int policyEnabled, out int changeReason);
        [PreserveSig] int SetSystemRadioState(int enabled);
        [PreserveSig] int Refresh();
        [PreserveSig] int OnHardwareSliderChange(int a, int b);
    }

    // ---------------- energy saver / battery ----------------

    public static bool IsEnergySaverOn()
    {
        try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
        catch { return false; }
    }

    /// <summary>Battery percentage and whether it's charging, or null on a PC without a battery.</summary>
    public static (int Percent, bool Charging)? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var ps) || ps.BatteryFlag == 128 || ps.BatteryFlag == 255 || ps.BatteryLifePercent > 100) return null;
        return (ps.BatteryLifePercent, ps.ACLineStatus == 1);
    }

    // ---------------- brightness (built-in screens) ----------------

    /// <summary>Brightness 0..100 of a built-in screen, or null (e.g. a desktop with external monitors).</summary>
    public static int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void SetBrightness(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject o in searcher.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------------- live captions ----------------

    public static bool IsLiveCaptionsOn() => Process.GetProcessesByName("LiveCaptions").Length > 0;

    /// <summary>Win+Ctrl+L toggles Windows' Live captions.</summary>
    public static void ToggleLiveCaptions()
    {
        Native.SendKeys(Native.VK_LWIN, Native.VK_CONTROL, 0x4C /* L */);
    }

    // ---------------- settings pages ----------------

    public static void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }
}
