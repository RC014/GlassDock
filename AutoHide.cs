using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Slides one bar in when the cursor touches the bottom edge of the screen inside that bar's trigger zone, and
/// out again shortly after the cursor leaves it. Each bar has its own controller, so the dock and the status bar
/// open independently. The bars float over other windows; nothing is reserved.
/// </summary>
internal sealed class AutoHide
{
    private const double ShowSeconds = 0.22, HideSeconds = 0.15;
    private static readonly TimeSpan EdgeDwell = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MinShown = TimeSpan.FromMilliseconds(450);

    private readonly GlassWindow[] _bars;
    private readonly Func<Native.POINT, bool> _inZone;
    private readonly Func<bool> _keepOpen;
    // Normal priority: above the screen sampler (Background), so showing/hiding never waits behind it.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private bool _shown = true;
    private TimeSpan _lastInside;
    private TimeSpan? _edgeSince;
    private IntPtr _lastForeground;
    private static readonly uint OurProcessId = (uint)Environment.ProcessId;

    private static bool IsOurWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == OurProcessId;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <param name="bars">The bar plus any companions anchored to it (e.g. the media bubble next to the dock); they slide together.</param>
    /// <param name="inZone">Whether a cursor on the bottom edge (physical px) should reveal this bar.</param>
    /// <param name="keepOpen">Keeps the bar up regardless of the cursor, e.g. while its menu is open.</param>
    public AutoHide(GlassWindow[] bars, Func<Native.POINT, bool> inZone, Func<bool> keepOpen)
    {
        _bars = bars;
        _inZone = inZone;
        _keepOpen = keepOpen;
        // Stay up briefly after start-up so it's clear GlassDock is running.
        _lastInside = _clock.Elapsed + TimeSpan.FromSeconds(1.5);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private void Poll()
    {
        var now = _clock.Elapsed;
        Native.GetCursorPos(out var p);
        bool atEdge = p.Y >= Native.GetSystemMetrics(Native.SM_CYSCREEN) - 2 && _inZone(p);

        if (_shown)
        {
            bool onBar = Array.Exists(_bars, b => b.ContainsCursor(p));

            // Switching to another window (with the cursor away from the bar) means you're done with it: close
            // its menus/flyout and let it hide now, rather than waiting on anything that might hold it open.
            var fg = Native.GetForegroundWindow();
            if (fg != _lastForeground)
            {
                _lastForeground = fg;
                if (!onBar && !atEdge && !IsOurWindow(fg))
                {
                    foreach (var bar in _bars) bar.CloseTransients();
                    _lastInside = TimeSpan.Zero;
                }
            }

            bool inside = atEdge || onBar || _keepOpen();
            if (inside) _lastInside = Max(_lastInside, now);
            else if (now - _lastInside > HideDelay) SetShown(false);
        }
        else if (atEdge && !ForegroundIsFullScreen())
        {
            _edgeSince ??= now;
            if (now - _edgeSince >= EdgeDwell)
            {
                // Once opened, stay up a moment even if the cursor twitches off the edge right away.
                _lastInside = now + MinShown;
                SetShown(true);
            }
        }
        else _edgeSince = null;
    }

    private void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        _edgeSince = null;
        // Once fully off-screen, hide the windows so they cost nothing (unless it was re-shown meanwhile).
        foreach (var bar in _bars)
            bar.Slide(shown, shown ? ShowSeconds : HideSeconds, shown ? null : () => { if (!_shown) bar.Hide(); });
    }

    /// <summary>Don't pop up over full-screen games and videos.</summary>
    internal static bool ForegroundIsFullScreen()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        Native.GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

        // Windows' own signal for exclusive full-screen games and presentation mode.
        if (SHQueryUserNotificationState(out int state) == 0 && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;

        // Maximised windows cover the whole screen too (there's no taskbar), so they don't count;
        // only borderless windows that fill the screen, like videos and borderless games, do.
        if (IsZoomed(fg) || (Native.GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
        return Native.GetWindowRect(fg, out var r) && r.Left <= 0 && r.Top <= 0 &&
               r.Right >= Native.GetSystemMetrics(Native.SM_CXSCREEN) && r.Bottom >= Native.GetSystemMetrics(Native.SM_CYSCREEN);
    }

    private const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
