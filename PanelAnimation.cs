using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Open / close animation for the glass panels (Start menu, Quick Settings): they rise into place while fading in,
/// and sink a little while fading out. The fade is done by Windows on the whole window (a layered window with
/// constant alpha, composed by DWM), so the panels stay ordinary hardware-rendered windows and the glass, its
/// shadow and rounded corners fade together.
/// </summary>
internal sealed class PanelAnimation
{
    private const double Rise = 18, Sink = 12;      // DIPs
    /// <summary>Furthest a panel is below its resting place during an animation, in DIPs.</summary>
    public const double MaxTravel = Rise;
    private const double OpenMs = 200, CloseMs = 140;

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 2;

    private readonly Window _window;
    private readonly Stopwatch _clock = new();
    private EventHandler? _frame;

    public PanelAnimation(Window window) => _window = window;

    /// <summary>Where the panel rests when open (window Top, DIPs).</summary>
    public double RestTop { get; private set; }

    /// <summary>How far below its resting place the panel is right now (DIPs; 0 when not animating).</summary>
    public double OffsetDip => _window.IsVisible ? Math.Clamp(_window.Top - RestTop, 0, MaxTravel) : 0;

    /// <summary>True while the close animation runs (the window is still visible).</summary>
    public bool IsClosing { get; private set; }

    /// <summary>Shows the window (or brings it back if it's closing), rising into place at <paramref name="top"/>.</summary>
    public void Show(double top)
    {
        Stop();
        IsClosing = false;
        RestTop = top;
        if (Settings.Current.LowPowerMode)
        {
            SetAlpha(1);
            _window.Top = top;
            if (!_window.IsVisible) _window.Show();
            return;
        }
        double startAlpha = _window.IsVisible ? Alpha : 0;
        SetAlpha(startAlpha);
        double startTop = _window.IsVisible ? _window.Top : top + Rise;
        _window.Top = startTop;
        if (!_window.IsVisible) _window.Show();
        Run(OpenMs, t =>
        {
            double e = 1 - Math.Pow(1 - t, 3); // ease-out
            _window.Top = startTop + (top - startTop) * e;
            SetAlpha(startAlpha + (1 - startAlpha) * e);
        }, null);
    }

    /// <summary>Sinks and fades the window out, then hides it.</summary>
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
        double top = _window.Top, startAlpha = Alpha;
        RestTop = top;
        Run(CloseMs, t =>
        {
            double e = t * t; // ease-in
            _window.Top = top + Sink * e;
            SetAlpha(startAlpha * (1 - e));
        }, () =>
        {
            _window.Hide();
            _window.Top = top;
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

    private void Run(double ms, Action<double> step, Action? done)
    {
        _clock.Restart();
        step(0);
        _frame = (_, _) =>
        {
            double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / ms);
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
    }
}

/// <summary>A window opened and closed with a <see cref="PanelAnimation"/> (its glass follows the animation).</summary>
internal interface IAnimatedPanel
{
    PanelAnimation Animation { get; }
}
