using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Base for the two bars: a transparent (per-pixel alpha) topmost, non-activating window that draws the glass
/// tint, sheen and anti-aliased rim, with a <see cref="Backdrop"/> window underneath providing the live blur.
/// </summary>
public abstract class GlassWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020;
    protected static readonly uint WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");

    public static double BarHeight => Math.Round(Settings.Current.IconSize + 22);

    private readonly Backdrop _backdrop = new();
    private readonly TranslateTransform _slide = new();

    protected IntPtr Handle { get; private set; }

    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        UseLayoutRounding = true;

        // Owned windows always stay above their owner, so the bar is drawn over its blur.
        new WindowInteropHelper(this).Owner = _backdrop.Handle;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
            // The refraction samples the screen behind the bars; keep the bars themselves out of that capture.
            if (Settings.Current.Refraction)
            {
                Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
                Native.SetWindowDisplayAffinity(_backdrop.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            }
            HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
            Relayout();
        };
        Loaded += (_, _) => Relayout();
        ContentRendered += (_, _) => Relayout();
        SizeChanged += (_, _) => Relayout();
        DpiChanged += (_, _) => Relayout();
        LocationChanged += (_, _) => SyncBackdrop();
        IsVisibleChanged += (_, _) => _backdrop.SetVisible(IsVisible);
        Closed += (_, _) => _backdrop.Dispose();

        // "Show desktop" / Win+M must never minimise the bars.
        StateChanged += (_, _) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
    }

    /// <summary>True while the bar is (or is sliding) in view.</summary>
    internal bool IsSlidIn { get; private set; } = true;

    /// <summary>How far the bar travels to leave the screen: the window reaches the screen's bottom edge.</summary>
    private static double SlideDistance => BarHeight + Settings.Current.BottomMargin + 4;

    // The window never moves (moving a WPF layered window every frame showed ghost copies at stale positions).
    // Instead the content slides inside it: _slide carries the vertical slide and the sideways shift (ShiftX).
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is FrameworkElement root)
        {
            root.RenderTransform = _slide;
            root.Margin = new Thickness(0, 0, 0, Settings.Current.BottomMargin);
        }
    }

    /// <summary>The window's rectangle in physical px.</summary>
    internal Native.RECT RestRectPx { get; private set; }

    /// <summary>False while the bar has nothing to show (e.g. the media bubble with nothing playing).</summary>
    internal virtual bool CanShow => true;

    internal void RelayoutNow() => Relayout();

    /// <summary>Current horizontal shift (DIPs) of the bar's content inside its window.</summary>
    internal double ShiftDip { get; private set; }

    /// <summary>Shifts the bar sideways inside its window (no window move), e.g. to follow the dock's stretch.</summary>
    internal void ShiftX(double dip)
    {
        if (Math.Abs(dip - ShiftDip) < 0.05) return;
        ShiftDip = dip;
        _slide.X = dip;
        _backdrop.SetShift(dip * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    // ---------------- slide in / out ----------------
    // The content slides with a WPF animation (render thread, smooth timing) and the blur window's visual slides
    // with the same timing in the compositor. While sliding, the content is rendered once into a bitmap cache at
    // device resolution, so the glass effects (refraction, blur passes, glow) aren't recomputed every frame;
    // nothing inside the bar changes mid-slide (see ScreenSampler, the dock's magnifier) to keep that cache valid.

    private int _slideVersion;

    /// <summary>True while a slide animation is running.</summary>
    internal bool IsSliding { get; private set; }

    /// <summary>Slides the bar into view or off the bottom of the screen.</summary>
    internal void Slide(bool shown, double seconds, Action? completed = null)
    {
        if (shown && !CanShow) return;
        IsSlidIn = shown;
        if (shown && !IsVisible) Show();

        int version = ++_slideVersion;
        IsSliding = true;
        if (Content is FrameworkElement root && root.CacheMode == null)
            root.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX) { SnapsToDevicePixels = true };

        double to = shown ? 0 : SlideDistance;
        // a partial slide (reversed midway) takes proportionally less time
        double secs = Math.Max(0.01, seconds * Math.Abs(to - _slide.Y) / SlideDistance);
        var anim = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromSeconds(secs))
        {
            EasingFunction = shown
                ? new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                : new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        };
        _slide.BeginAnimation(TranslateTransform.YProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        _backdrop.SlideTo(to * VisualTreeHelper.GetDpi(this).DpiScaleY, secs, easeOut: shown);

        // Finish on a timer rather than the animation's Completed event, which didn't always arrive (the media
        // bubble then stayed frozen on its slide bitmap and never hid). Back to live rendering, then e.g. hide.
        var finish = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(secs + 0.03),
        };
        finish.Tick += (_, _) =>
        {
            finish.Stop();
            if (version != _slideVersion) return; // superseded by a newer slide
            IsSliding = false;
            if (Content is FrameworkElement r) r.CacheMode = null;
            completed?.Invoke();
        };
        finish.Start();
    }


    /// <summary>The glass shape, in DIPs relative to the window.</summary>
    protected abstract Rect GlassRect { get; }

    protected abstract void Reposition();

    protected void Relayout()
    {
        if (Handle == IntPtr.Zero) return;
        Reposition();
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int left = (int)Math.Round(Left * s), top = (int)Math.Round(Top * s);
        RestRectPx = new Native.RECT { Left = left, Top = top, Right = left + (int)Math.Round(ActualWidth * s), Bottom = top + (int)Math.Round(ActualHeight * s) };
        SyncBackdrop();
    }

    protected void SyncBackdrop()
    {
        if (Handle == IntPtr.Zero || !Native.GetWindowRect(Handle, out var r)) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var g = GlassRect;
        _backdrop.Update(r, new Rect(g.X * s, g.Y * s, g.Width * s, g.Height * s), Settings.Current.CornerRadius * s);
    }

    // Context menus opened from this bar. Checked by asking the menus themselves (not by counting open/close
    // events), so a missed event can never leave the bar thinking a menu is still open.
    private readonly System.Collections.Generic.List<System.Windows.Controls.ContextMenu> _menus = new();

    internal void TrackMenu(System.Windows.Controls.ContextMenu menu) => _menus.Add(menu);

    /// <summary>Whether a context menu from this bar is actually open; auto-hide keeps the bar up meanwhile.</summary>
    internal bool HasOpenMenu
    {
        get
        {
            _menus.RemoveAll(m => !m.IsOpen);
            return _menus.Count > 0;
        }
    }

    /// <summary>Closes this bar's menus and other transient popups (e.g. when you switch to another window).</summary>
    internal virtual void CloseTransients()
    {
        foreach (var m in _menus.ToArray()) m.IsOpen = false;
        _menus.Clear();
    }

    /// <summary>Whether a physical screen x-coordinate lies under this bar's glass, widened by padDip on each side.
    /// Works while the bar is hidden too (it keeps its last position).</summary>
    internal bool GlassSpanContains(int x, double padDip)
    {
        if (Handle == IntPtr.Zero) return false;
        var r = RestRectPx;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var g = GlassRect;
        double left = r.Left + (g.X + ShiftDip - padDip) * s, right = r.Left + (g.Right + ShiftDip + padDip) * s;
        return x >= left && x <= right;
    }

    /// <summary>The area (DIPs, relative to the window) that counts as "on the bar" for auto-hide.</summary>
    protected virtual Rect HoverRect => GlassRect;

    /// <summary>Whether the cursor (physical screen px) is over the bar where it rests, including the gap below it
    /// down to the screen edge (not its transparent padding to the sides or above).</summary>
    internal bool ContainsCursor(Native.POINT p)
    {
        if (!IsVisible || Handle == IntPtr.Zero) return false;
        var r = RestRectPx;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var h = HoverRect;
        return p.X >= r.Left + (h.Left + ShiftDip) * s && p.X < r.Left + (h.Right + ShiftDip) * s &&
               p.Y >= r.Top + h.Top * s && p.Y < r.Bottom;
    }

    /// <summary>Screen size in DIPs (primary monitor).</summary>
    protected static Size Screen => new(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SYSCOMMAND when (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE:
                handled = true;
                break;
            case WM_SETTINGCHANGE:
                Theme.Refresh();
                break;
            case WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(Relayout);
                break;
        }
        return IntPtr.Zero;
    }
}