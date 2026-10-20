using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassDock;

/// <summary>
/// Gets GlassDock's windows drawn again when WPF fails to put them on screen, as it can for a while after the PC
/// wakes from sleep or hibernation (the bars then showed up without their animation, and the Start menu and Quick
/// Settings opened without appearing).
/// <para>
/// When a window's frame doesn't reach the screen, WPF's render thread sends the window a "NeedsRePresentOnWake"
/// message. WPF retries only within 10 s of a wake or unlock, and a window created after the wake gets a single retry;
/// after that the window keeps its old picture (or none) until the next wake or unlock. Here every failure gets the
/// window redrawn again half a second later, for as long as it keeps failing. Failures and recoveries, and wakes and
/// unlocks, are noted in dock.log.
/// </para>
/// </summary>
internal static class RenderWatch
{
    private static readonly int NeedsRePresent = RegisterWindowMessage("NeedsRePresentOnWake");
    private static readonly int DisplaysChanged = RegisterWindowMessage("DisplayDevicesAvailabilityChanged");
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    /// <summary>A window counts as drawing again when this long passes after a redraw without a new failure.</summary>
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(2);

    private sealed class Watch
    {
        public required Window Window;
        public required IntPtr Hwnd;
        public bool Failing, DisplaysLost;
        public int Retries;
        public DateTime LastFailure, LastRetry;
        public DispatcherTimer? Timer;
    }

    private static readonly Dictionary<IntPtr, Watch> Watched = new();

    public static void Start()
    {
        // every GlassDock window: those already up, and the others as they're shown for the first time
        foreach (Window w in Application.Current.Windows) Attach(w);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Attach((Window)s)));
        // (these arrive on another thread)
        SystemEvents.PowerModeChanged += (_, a) =>
        {
            if (a.Mode is PowerModes.Resume or PowerModes.Suspend) Note(a.Mode == PowerModes.Resume ? "woke up" : "going to sleep");
        };
        SystemEvents.SessionSwitch += (_, a) =>
        {
            if (a.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLock) Note(a.Reason == SessionSwitchReason.SessionLock ? "locked" : "unlocked");
        };
    }

    private static void Note(string text) => Application.Current.Dispatcher.BeginInvoke(() => ClickLog.Line(text));

    private static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || Watched.ContainsKey(source.Handle)) return;
        var watch = new Watch { Window = window, Hwnd = source.Handle };
        Watched[source.Handle] = watch;
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == NeedsRePresent) OnFrameFailed(watch);
            else if (msg == DisplaysChanged)
            {
                // (every new window hears "available" once: only note losing them, and getting them back)
                bool available = wParam != IntPtr.Zero;
                if (!available || watch.DisplaysLost) ClickLog.Line($"displays {(available ? "available again" : "unavailable")} ({Name(watch)})");
                watch.DisplaysLost = !available;
            }
            return IntPtr.Zero; // WPF still handles both itself
        });
        source.Disposed += (_, _) => { watch.Timer?.Stop(); Watched.Remove(watch.Hwnd); };
    }

    private static void OnFrameFailed(Watch watch)
    {
        watch.LastFailure = DateTime.Now;
        if (!watch.Failing)
        {
            watch.Failing = true;
            watch.Retries = 0;
            watch.LastRetry = DateTime.MinValue;
            ClickLog.Line($"drawing failed: {Name(watch)}");
        }
        if (watch.Timer == null)
        {
            watch.Timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = RetryDelay };
            watch.Timer.Tick += (_, _) => Retry(watch);
        }
        watch.Timer.Start();
    }

    private static void Retry(Watch watch)
    {
        // a hidden window isn't drawn at all; WPF draws it afresh when it's shown again
        if (!IsWindowVisible(watch.Hwnd)) { watch.Timer!.Stop(); watch.Failing = false; return; }
        if (watch.LastFailure > watch.LastRetry)
        {
            // failed (again, after the last redraw): redraw the whole window
            watch.Retries++;
            watch.LastRetry = DateTime.Now;
            InvalidateRect(watch.Hwnd, IntPtr.Zero, true);
            watch.Window.InvalidateVisual();
        }
        else if (DateTime.Now - watch.LastRetry >= HealthyAfter)
        {
            watch.Timer!.Stop();
            watch.Failing = false;
            ClickLog.Line($"drawing works again: {Name(watch)} (after {watch.Retries} redraws)");
        }
    }

    private static string Name(Watch watch) => string.IsNullOrEmpty(watch.Window.Title) ? watch.Window.GetType().Name : watch.Window.Title;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
