using System;
using System.Runtime.InteropServices;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Replaces Windows' volume / brightness pop-up with GlassDock's glass one, however it was triggered (keyboard
/// keys GlassDock didn't catch, a laptop's Fn keys, brightness keys handled by the firmware...). Watches, from
/// outside, for explorer showing its pop-up window (a small XAML island at the bottom centre of the screen),
/// makes it transparent (so it never appears, not even for a frame) and shows the glass indicator with whichever
/// level just changed.
/// </summary>
internal static class WindowsOsd
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 2;
    private const string OsdClass = "XamlExplorerHostIslandWindow";

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; }

    private enum Kind { Volume, Brightness }

    private static IntPtr _hook;
    private static WinEventProc? _proc; // kept alive while hooked
    private static (float Level, bool Muted)? _lastVolume;
    private static int? _lastBrightness;
    private static Kind _lastKind = Kind.Volume;
    private static DateTime _lastShown;
    private static readonly DispatcherTimer _volumeSnapshot = new() { Interval = TimeSpan.FromSeconds(1) };
    private static DateTime _brightnessChangedAt, _pendingSince;
    private static ManagementEventWatcher? _brightnessWatcher;
    private static Dispatcher _ui = null!;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnShow;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // Make the pop-up invisible before it's ever shown, so it can't flash even for a frame.
        for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, OsdClass, null)) != IntPtr.Zero;)
            if (IsWindowsOsd(h)) MakeInvisible(h);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreWindowsPopup();
        // Volume changed elsewhere (Quick Settings, the mixer...) shouldn't later look like a key press.
        _volumeSnapshot.Tick += (_, _) => _lastVolume = Audio.Get();
        _volumeSnapshot.Start();
        _lastVolume = Audio.Get();
        _ = Task.Run(SystemControls.GetBrightness).ContinueWith(t => _lastBrightness = t.Result, TaskScheduler.Default);
        _ui = Dispatcher.CurrentDispatcher;
        StartBrightnessEvents();
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _volumeSnapshot.Stop();
        try { _brightnessWatcher?.Stop(); _brightnessWatcher?.Dispose(); } catch { }
        _brightnessWatcher = null;
        RestoreWindowsPopup();
    }

    private static void OnShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || !Settings.Current.GlassVolumeIndicator || !IsWindowsOsd(hwnd)) return;
        // Normally it's already invisible; if explorer made a new one, hide this showing and make it invisible.
        if (MakeInvisible(hwnd)) Native.ShowWindow(hwnd, Native.SW_HIDE);
        _ = ShowGlassAsync();
    }

    /// <summary>
    /// Makes the pop-up window fully transparent (and so click-through) while leaving it to explorer otherwise: it
    /// still "shows", but nothing appears on screen. Returns true if it wasn't transparent yet.
    /// </summary>
    private static bool MakeInvisible(IntPtr hwnd)
    {
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _) && alpha == 0) return false;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        return true;
    }

    /// <summary>Gives Windows its pop-up back (on quit, when the setting is turned off, and by --restore).</summary>
    public static void RestoreWindowsPopup()
    {
        try
        {
            for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, OsdClass, null)) != IntPtr.Zero;)
            {
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0) continue;
                SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            }
        }
        catch { }
    }

    /// <summary>Explorer's pop-up: a small XAML island window, centred near the bottom of its monitor.</summary>
    private static bool IsWindowsOsd(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != OsdClass) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/), ref mi)) return false;
        double scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
        double w = (r.Right - r.Left) / scale, h = (r.Bottom - r.Top) / scale;
        var m = mi.rcMonitor;
        double centreOffset = Math.Abs((r.Left + r.Right) / 2.0 - (m.Left + m.Right) / 2.0);
        return w is > 40 and < 460 && h is > 20 and < 150
            && centreOffset < (m.Right - m.Left) * 0.1
            && r.Bottom > m.Top + (m.Bottom - m.Top) * 0.7;
    }

    private static async Task ShowGlassAsync()
    {
        var volume = Audio.Get();
        bool volumeChanged = !Equals(volume, _lastVolume);
        _lastVolume = volume;
        if (volumeChanged) { Show(Kind.Volume); return; }

        // Brightness: Windows reports each change (with the new value) as a WMI event, which can arrive just before
        // or just after its pop-up shows.
        if ((DateTime.Now - _brightnessChangedAt).TotalSeconds < 1 && _lastBrightness != null)
        {
            Show(Kind.Brightness, _lastBrightness);
            return;
        }
        var pending = _pendingSince = DateTime.Now;
        await Task.Delay(600);
        if (_pendingSince != pending) return; // the brightness event came and showed it

        _pendingSince = DateTime.MinValue;
        // No event: fall back to reading the brightness. Nothing changed (e.g. already at 100%)? Repeat whatever
        // the last presses were about.
        int? brightness = await Task.Run(SystemControls.GetBrightness);
        bool brightnessChanged = brightness != null && brightness != _lastBrightness;
        _lastBrightness = brightness ?? _lastBrightness;
        var kind = brightnessChanged ? Kind.Brightness
            : (DateTime.Now - _lastShown).TotalSeconds < 3 ? _lastKind : Kind.Volume;
        Show(kind, _lastBrightness);
    }

    /// <summary>A brightness change reported by Windows (keys, settings, adaptive brightness...).</summary>
    private static void OnBrightnessChanged(int value)
    {
        _lastBrightness = value;
        _brightnessChangedAt = DateTime.Now;
        bool pending = (DateTime.Now - _pendingSince).TotalSeconds < 1;
        bool showingBrightness = _lastKind == Kind.Brightness && (DateTime.Now - _lastShown).TotalSeconds < 2;
        // Only together with Windows' pop-up (or while ours shows brightness, so it follows the fade and held keys):
        // changes Windows doesn't announce (adaptive brightness, our own slider) stay silent, as they do in Windows.
        if (pending) _pendingSince = DateTime.MinValue;
        if (pending || showingBrightness) Show(Kind.Brightness, value);
    }

    private static void StartBrightnessEvents()
    {
        try
        {
            _brightnessWatcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightnessWatcher.EventArrived += (_, e) =>
            {
                try
                {
                    int value = Convert.ToInt32(e.NewEvent["Brightness"]);
                    _ui.BeginInvoke(() => OnBrightnessChanged(value));
                }
                catch { }
            };
            _brightnessWatcher.Start();
        }
        catch (Exception ex) { App.Log(ex); } // no brightness control (desktop monitor): nothing to watch
    }

    private static void Show(Kind kind, int? brightness = null)
    {
        if (kind == Kind.Brightness && brightness == null) kind = Kind.Volume;
        _lastKind = kind;
        _lastShown = DateTime.Now;
        if (kind == Kind.Volume) App.ShowVolumeOsd();
        else App.ShowBrightnessOsd(brightness!.Value);
    }
}
