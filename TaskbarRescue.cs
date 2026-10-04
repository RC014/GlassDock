using System;
using System.IO;
using System.Runtime.InteropServices;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Safety net for the hidden Windows taskbar. The original auto-hide state is written to disk before hiding,
/// so "GlassDock.exe --restore" can bring the taskbar back even if GlassDock was killed.
/// </summary>
internal static class TaskbarRescue
{
    private static readonly string StateFile = Path.Combine(Settings.Folder, "taskbar-state.txt");

    public static void RememberOriginalState()
    {
        // If the file is still there, the previous run didn't exit cleanly: keep the state it recorded.
        if (File.Exists(StateFile)) return;
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = tray };
        int state = (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd);
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StateFile, state.ToString());
        }
        catch { }
    }

    public static void Restore()
    {
        WindowsOsd.RestoreWindowsPopup();
        RestoreTaskbar();
    }

    private static void RestoreTaskbar()
    {
        int state = Native.ABS_ALWAYSONTOP;
        try
        {
            if (File.Exists(StateFile) && int.TryParse(File.ReadAllText(StateFile).Trim(), out int s)) state = s;
        }
        catch { }

        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = tray, lParam = (IntPtr)state };
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
            Native.ShowWindow(tray, Native.SW_SHOW);
        }
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            Native.ShowWindow(secondary, Native.SW_SHOW);

        try { File.Delete(StateFile); } catch { }
    }
}
