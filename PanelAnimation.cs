using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Open / close animation for the glass panels (Start menu, Quick Settings). The window itself never moves: the
/// whole panel fades in where it rests (Windows fades the window, a layered window with constant alpha composed by
/// DWM, so glass, shadow and rounded corners fade together) while its contents rise into place; closing fades it
/// out while the contents sink a little. Keeping the window still keeps the glass's picture of the screen behind it
/// exact on every frame (a moving window is redrawn a frame after Windows moves it, so its glass would lag).
/// </summary>
internal sealed class PanelAnimation
{
    private const double Rise = 18, Sink = 12;      // DIPs
    private const double OpenMs = 200, CloseMs = 140;

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 2;

    private readonly Window _window;
    private readonly TranslateTransform _shift = new();
    private readonly Stopwatch _clock = new();
    private EventHandler? _frame;

    private readonly UIElement _body;
    private readonly GlassSurface _glass;

    /// <param name="body">The panel's contents (everything above the glass), which slide.</param>
    /// <param name="glass">The panel's glass, which stays put.</param>
    public PanelAnimation(Window window, UIElement body, GlassSurface glass)
    {
        _window = window;
        _body = body;
        _glass = glass;
        body.RenderTransform = _shift;
    }

    /// <summary>
    /// While animating, the glass and the contents are each drawn once into a cached bitmap, so a frame only moves
    /// the contents' bitmap and fades the window instead of redrawing text, icons and the refraction shaders.
    /// </summary>
    private void Cache(bool on)
    {
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        _body.CacheMode = on ? new BitmapCache(scale) : null;
        _glass.CacheMode = on ? new BitmapCache(scale) : null;
        _glass.IsAnimating = on;
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
        if (Settings.Current.LowPowerMode)
        {
            SetAlpha(1);
            _shift.Y = 0;
            if (!_window.IsVisible) _window.Show();
            return;
        }
        bool wasVisible = _window.IsVisible;
        double startAlpha = wasVisible ? Alpha : 0, startY = wasVisible ? _shift.Y : Rise;
        SetAlpha(startAlpha);
        _shift.Y = startY;
        if (!wasVisible) _window.Show();
        Run(OpenMs, t =>
        {
            double e = 1 - Math.Pow(1 - t, 3); // ease-out
            _shift.Y = startY * (1 - e);
            SetAlpha(startAlpha + (1 - startAlpha) * e);
        }, null);
    }

    /// <summary>Fades the window out while its contents sink, then hides it.</summary>
    public void Hide(Action? hidden = null)
    {
        if (!_window.IsVisible || IsClosing) return;
        Stop();
        if (Settings.Current.LowPowerMode)
        {
            _window.Hide();
            hidden?.Invoke();
            return;
        }
        IsClosing = true;
        double startAlpha = Alpha, startY = _shift.Y;
        Run(CloseMs, t =>
        {
            double e = t * t; // ease-in
            _shift.Y = startY + (Sink - startY) * e;
            SetAlpha(startAlpha * (1 - e));
        }, () =>
        {
            _window.Hide();
            _shift.Y = 0;
            SetAlpha(1);
            IsClosing = false;
            hidden?.Invoke();
        });
    }

    private double Alpha { get; set; } = 1;

    private void SetAlpha(double a)
    {
        Alpha = Math.Clamp(a, 0, 1);
        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0) Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(Alpha * 255), LWA_ALPHA);
    }

    /// <summary>Longest step the animation takes in one frame: a slow frame pauses it instead of making it jump ahead.</summary>
    private const double MaxStepMs = 1000 / 45.0;

    private readonly System.Collections.Generic.List<System.Threading.Tasks.TaskCompletionSource> _idleWaiters = new();

    /// <summary>Completes once the current open or close animation (if any) has finished.</summary>
    public System.Threading.Tasks.Task WhenIdleAsync()
    {
        if (_frame == null) return System.Threading.Tasks.Task.CompletedTask;
        var waiter = new System.Threading.Tasks.TaskCompletionSource();
        _idleWaiters.Add(waiter);
        return waiter.Task;
    }

    private void Run(double ms, Action<double> step, Action? done)
    {
        Cache(true);
        _clock.Restart();
        step(0);
        double elapsed = 0, last = 0;
        _frame = (_, _) =>
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            elapsed += Math.Min(now - last, MaxStepMs);
            last = now;
            double t = Math.Min(1, elapsed / ms);
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
        Cache(false);
        var waiters = _idleWaiters.ToArray();
        _idleWaiters.Clear();
        foreach (var w in waiters) w.TrySetResult();
    }
}
