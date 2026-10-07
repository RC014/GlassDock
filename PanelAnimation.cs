using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDock;

/// <summary>
/// Open / close animation for the glass panels (Start menu, Quick Settings). The window and its glass stay put
/// (so the glass's picture of the screen behind it is exact on every frame); the contents rise into place while
/// fading in, and sink a little while fading out before the panel goes away.
/// <para>
/// A panel is shown cloaked (hidden by DWM) and revealed only after WPF has drawn a fresh frame: otherwise Windows
/// briefly shows whatever the window drew last time it was visible (after the start-up prewarm, a black glass).
/// </para>
/// <para>
/// The glass stays cached as a bitmap while the panel is open, so hovering, scrolling or typing only redraws the
/// contents, not the refraction shaders underneath (which re-run only when the screen behind actually changes).
/// </para>
/// </summary>
internal sealed class PanelAnimation
{
    private const double Rise = 18, Sink = 12;      // DIPs
    private const double OpenMs = 200, CloseMs = 140;
    /// <summary>Longest step the animation takes in one frame: a slow frame pauses it instead of making it jump ahead.</summary>
    private const double MaxStepMs = 1000 / 45.0;
    /// <summary>Frames to wait after showing a panel before revealing it (the first one has been drawn by then).</summary>
    private const int RevealAfterFrames = 2;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_CLOAK = 13;

    private readonly Window _window;
    private readonly UIElement _body;
    private readonly GlassSurface _glass;
    private readonly TranslateTransform _shift = new();
    private readonly Stopwatch _clock = new();
    private readonly List<TaskCompletionSource> _idleWaiters = new();
    private EventHandler? _frame;
    private bool _cloaked;

    /// <param name="body">The panel's contents (everything above the glass), which slide and fade.</param>
    /// <param name="glass">The panel's glass, which stays put.</param>
    public PanelAnimation(Window window, UIElement body, GlassSurface glass)
    {
        _window = window;
        _body = body;
        _glass = glass;
        body.RenderTransform = _shift;
        window.SourceInitialized += (_, _) => CacheGlass();
        window.DpiChanged += (_, _) => CacheGlass();
    }

    /// <summary>True while an open or close animation runs.</summary>
    public bool IsAnimating => _frame != null;

    /// <summary>True while the close animation runs (the window is still visible).</summary>
    public bool IsClosing { get; private set; }

    /// <summary>Shows the window at <paramref name="top"/> (or brings it back if it's closing).</summary>
    public void Show(double top)
    {
        Stop();
        IsClosing = false;
        _window.Top = top;
        if (!_window.IsVisible)
        {
            SetCloaked(true);
            _body.Opacity = 0;
            _shift.Y = Rise;
            _window.Show();
        }
        double startOpacity = _body.Opacity, startY = _shift.Y;
        Run(Settings.Current.LowPowerMode ? 0 : OpenMs, t =>
        {
            double e = 1 - Math.Pow(1 - t, 3); // ease-out
            _shift.Y = startY * (1 - e);
            _body.Opacity = startOpacity + (1 - startOpacity) * e;
        }, null);
    }

    /// <summary>Fades the contents out while they sink, then hides the window.</summary>
    public void Hide(Action? hidden = null)
    {
        if (!_window.IsVisible || IsClosing) return;
        Stop();
        void Finish()
        {
            _window.Hide();
            _shift.Y = 0;
            _body.Opacity = 1;
            IsClosing = false;
            hidden?.Invoke();
        }
        if (Settings.Current.LowPowerMode) { Finish(); return; }
        IsClosing = true;
        double startOpacity = _body.Opacity, startY = _shift.Y;
        Run(CloseMs, t =>
        {
            double e = t * t; // ease-in
            _shift.Y = startY + (Sink - startY) * e;
            _body.Opacity = startOpacity * (1 - e);
        }, Finish);
    }

    /// <summary>Completes once the current open or close animation (if any) has finished.</summary>
    public Task WhenIdleAsync()
    {
        if (_frame == null) return Task.CompletedTask;
        var waiter = new TaskCompletionSource();
        _idleWaiters.Add(waiter);
        return waiter.Task;
    }

    private void Run(double ms, Action<double> step, Action? done)
    {
        // While animating, the contents are cached too (a frame then only moves and fades their bitmap), and the
        // glass takes no new screen samples (so its cached bitmap stays valid).
        _body.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(_window).DpiScaleX);
        _glass.IsAnimating = true;
        step(0);
        int waitFrames = _cloaked ? RevealAfterFrames : 0;
        double elapsed = 0, last = -1;
        _frame = (_, _) =>
        {
            if (waitFrames > 0)
            {
                if (--waitFrames == 0) SetCloaked(false);
                return;
            }
            if (last < 0) { _clock.Restart(); last = 0; } // the animation starts once the panel is on screen
            double now = _clock.Elapsed.TotalMilliseconds;
            elapsed += Math.Min(now - last, MaxStepMs);
            last = now;
            double t = ms <= 0 ? 1 : Math.Min(1, elapsed / ms);
            step(t);
            if (t < 1) return;
            Stop();
            done?.Invoke();
        };
        CompositionTarget.Rendering += _frame;
    }

    private void Stop()
    {
        if (_frame == null) return;
        CompositionTarget.Rendering -= _frame;
        _frame = null;
        _body.CacheMode = null;
        _glass.IsAnimating = false;
        var waiters = _idleWaiters.ToArray();
        _idleWaiters.Clear();
        foreach (var w in waiters) w.TrySetResult();
    }

    private void CacheGlass() => _glass.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(_window).DpiScaleX);

    private void SetCloaked(bool on)
    {
        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        int value = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
        _cloaked = on;
    }
}
