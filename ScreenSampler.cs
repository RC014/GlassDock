using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GlassDock;

/// <summary>
/// Copies the screen behind each visible glass surface EffectiveRefractionFps times a second (20 by default), so the lens
/// shader has a real image to refract. The bars exclude themselves from screen capture (see GlassWindow), so they
/// never sample themselves.
/// </summary>
internal static class ScreenSampler
{
    private static readonly List<GlassSurface> Surfaces = new();
    private static DispatcherTimer? _timer;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double _lastSampleMs = double.NegativeInfinity;
    private static double IntervalMs => 1000 / Settings.Current.EffectiveRefractionFps;

    public static void Register(GlassSurface surface)
    {
        Surfaces.Add(surface);
        if (_timer != null) return;
        // While glass is on screen, sampling is driven by WPF's frame loop (TrackFrame), so busy UI work can't
        // starve it. This timer only notices glass appearing while that loop is off; it runs below the
        // auto-hide checks (Normal priority) so they never wait for a screen copy.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
        _timer.Tick += (_, _) => TickIfDue();
        _timer.Start();
    }

    private static bool _tracking;

    /// <summary>Every frame: keep each visible surface's brush aligned with where its glass is right now, and take
    /// a new screen sample when one is due.</summary>
    private static void TrackFrame(object? sender, EventArgs e)
    {
        foreach (var s in Surfaces)
            if (s.NeedsSample) s.UpdateViewbox();
        TickIfDue();
    }

    // A few ms of slack so e.g. 30 fps on a 60 Hz screen samples on every other frame, not every third.
    private static void TickIfDue()
    {
        if (Clock.Elapsed.TotalMilliseconds - _lastSampleMs >= IntervalMs - 4) Tick();
    }

    /// <summary>Per-frame tracking only while some bar is on screen (it keeps WPF's render loop awake).</summary>
    private static void SetTracking(bool on)
    {
        if (on == _tracking) return;
        _tracking = on;
        if (on) System.Windows.Media.CompositionTarget.Rendering += TrackFrame;
        else System.Windows.Media.CompositionTarget.Rendering -= TrackFrame;
    }

    public static void Unregister(GlassSurface surface) => Surfaces.Remove(surface);

    /// <summary>Samples one surface right away (e.g. as its bar appears) instead of waiting for the next tick.</summary>
    public static void SampleNow(GlassSurface surface)
    {
        if (!surface.NeedsSample) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        try { surface.Sample(screen); }
        catch (Exception ex) { App.Log(ex); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        SetTracking(true);
    }

    private static void Tick()
    {
        _lastSampleMs = Clock.Elapsed.TotalMilliseconds;
        IntPtr screen = IntPtr.Zero;
        bool any = false;
        try
        {
            foreach (var s in Surfaces)
            {
                if (!s.NeedsSample) continue;
                any = true;
                if (s.IsSliding) continue;
                if (screen == IntPtr.Zero) screen = GetDC(IntPtr.Zero);
                s.Sample(screen);
            }
            SetTracking(any);
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
