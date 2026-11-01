using System;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using GlassDock.Interop;
using ManagedShell.WindowsTray;
using static ManagedShell.Interop.NativeMethods;

namespace GlassDock;

/// <summary>
/// Tells apps that ask that the taskbar auto-hides at the bottom of the screen - which GlassDock's bars do - so
/// maximised apps leave the one-pixel gap there that they leave for Windows' own auto-hiding taskbar.
/// <para>
/// Apps that draw their own window frame (Telegram, Chromium-based browsers, Electron apps) ask the taskbar, each time
/// they lay out a maximised window, whether it auto-hides and on which edge. The question goes to the topmost
/// Shell_TrayWnd, which is GlassDock's (ManagedShell's tray window), and ManagedShell answered "doesn't auto-hide".
/// Maximised apps then covered the whole screen exactly. Qt 6.11, which Telegram uses, takes a window that covers the
/// whole screen for a full-screen one: Telegram then made its whole window client area, 11 px bigger than the screen on
/// every side, which no longer covered the screen exactly, so it went back, and so on - Telegram resized itself each
/// time its title changed (a new message, another chat). With the gap, a maximised window never covers the whole screen.
/// </para>
/// <para>
/// The answers are only given while GlassDock's bars don't reserve screen space; ManagedShell's own question about
/// Explorer's taskbar, and every other message, are still handled by ManagedShell.
/// </para>
/// </summary>
internal static class TaskbarAnswers
{
    private static NotificationArea? _area;
    private static AppBarMessageDelegate? _managedShell;
    private static AppBarMessageDelegate? _ours; // kept alive: the tray calls it from native code

    public static void Install(NotificationArea area)
    {
        // ManagedShell's own handler (set by its AppBarManager), for every message not answered here
        _managedShell = typeof(NotificationArea)
            .GetField("appBarMessageDelegate", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(area) as AppBarMessageDelegate;
        if (_managedShell == null)
        {
            App.Log(new InvalidOperationException("TaskbarAnswers: ManagedShell's AppBar handler wasn't found; taskbar answers left as they are."));
            return;
        }
        _area = area;
        // Where the taskbar is (ABM_GETTASKBARPOS): the bottom, where GlassDock's bars are. ManagedShell's default was a
        // strip at the top.
        area.SetTrayHostSizeData(new TrayHostSizeData { edge = ABEdge.ABE_BOTTOM, rc = BottomStrip() });
        _ours = OnMessage;
        area.SetAppBarMessageCallback(_ours);
    }

    private static IntPtr OnMessage(APPBARMSGDATAV3 amd, ref bool handled)
    {
        bool autoHides = Settings.Current.AutoHide || !Settings.Current.ReserveScreenSpace;
        switch ((ABMsg)amd.dwMessage)
        {
            case ABMsg.ABM_GETSTATE when autoHides && !AsksAboutExplorer(amd):
                handled = true;
                return (IntPtr)ABState.AutoHide;
            case ABMsg.ABM_GETAUTOHIDEBAR when autoHides:
            case ABMsg.ABM_GETAUTOHIDEBAREX when autoHides:
                // The auto-hiding bar on that edge (of that screen, for ...EX): GlassDock's tray window, since some apps
                // check that it's a Shell_TrayWnd.
                handled = true;
                bool bottom = amd.abd.uEdge == (uint)ABEdge.ABE_BOTTOM;
                bool primary = (ABMsg)amd.dwMessage == ABMsg.ABM_GETAUTOHIDEBAR || OnPrimaryScreen(amd.abd.rc);
                return bottom && primary ? _area!.Handle : IntPtr.Zero;
            default:
                return _managedShell!(amd, ref handled);
        }
    }

    /// <summary>The question names Explorer's taskbar (as ManagedShell asks for its state before hiding it).</summary>
    private static bool AsksAboutExplorer(APPBARMSGDATAV3 amd)
    {
        var hwnd = (IntPtr)amd.abd.hWnd;
        if (hwnd == IntPtr.Zero || amd.abd.hWnd == (uint)_area!.Handle) return false;
        var cls = new StringBuilder(32);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() == "Shell_TrayWnd";
    }

    private static bool OnPrimaryScreen(ManagedShell.Interop.NativeMethods.Rect rc)
    {
        int w = Native.GetSystemMetrics(Native.SM_CXSCREEN), h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        return rc.Left < w && rc.Right > 0 && rc.Top < h && rc.Bottom > 0;
    }

    /// <summary>The bottom of the primary screen, as tall as GlassDock's bars (physical px).</summary>
    private static ManagedShell.Interop.NativeMethods.Rect BottomStrip()
    {
        int w = Native.GetSystemMetrics(Native.SM_CXSCREEN), h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        double scale = App.Status is { } status ? VisualTreeHelper.GetDpi(status).DpiScaleY : 1;
        int bar = (int)Math.Round((GlassWindow.BarHeight + Settings.Current.BottomMargin) * scale);
        return new ManagedShell.Interop.NativeMethods.Rect { Left = 0, Top = h - bar, Right = w, Bottom = h };
    }
}
