using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GlassDock;

/// <summary>
/// Copies the screen behind each visible glass surface ~20 times a second, so the lens shader has a real image
/// to refract. The bars exclude themselves from screen capture (see GlassWindow), so they never sample themselves.
/// </summary>
internal static class ScreenSampler
{
    private static readonly List<GlassSurface> Surfaces = new();
    private static DispatcherTimer? _timer;

    public static void Register(GlassSurface surface)
    {
        Surfaces.Add(surface);
        if (_timer != null) return;
        // Background priority: copying screen pixels is slow (GPU readback), so it must never delay input
        // handling or the auto-hide checks; ~20 samples a second is plenty for what's behind a bar.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private static bool _tracking;

    /// <summary>Every frame: keep each visible surface's brush aligned with where its glass is right now.</summary>
    private static void TrackFrame(object? sender, EventArgs e)
    {
        foreach (var s in Surfaces)
            if (s.NeedsSample) s.UpdateViewbox();
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
