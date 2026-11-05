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

    // The bars are ordinary windows that DWM composes with their see-through pixels, like the panels (GlassTile). A
    // layered window (AllowsTransparency) has its every frame read back from the graphics card, which waits for all
    // the card's other work: over an app keeping the card busy, the dock's wave fell to a few frames a second.
    // WPF's software rendering can't draw see-through pixels into a normal window, so without a card they stay layered.
    private static readonly bool Layered = RenderCapability.Tier >> 16 == 0;

    private readonly Backdrop _backdrop = new();
    private readonly TranslateTransform _slide = new();

    protected IntPtr Handle { get; private set; }

    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = Layered;
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
            if (!Layered)
            {
                var all = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(Handle, ref all);
                if (HwndSource.FromHwnd(Handle) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
                int v = Native.DWMWCP_DONOTROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
                v = Native.DWMWA_COLOR_NONE;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, sizeof(int));
            }
            HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
            Relayout();
        };
        Loaded += (_, _) => Relayout();
        ContentRendered += (_, _) => Relayout();
        SizeChanged += (_, _) => Relayout();
        DpiChanged += (_, _) => Relayout();
        LocationChanged += (_, _) => SyncBackdrop();
        IsVisibleChanged += (_, _) => { _backdrop.SetVisible(IsVisible); HoldSize(!IsVisible); };
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

    // A hidden bar's window keeps its size. Each change of size of a window that isn't being drawn costs WPF a new
    // drawing surface (about 1 MB for the dock), and it frees the old ones only when the window is next drawn: with
    // an app opening and closing windows and the dock not opened meanwhile, that added up to gigabytes.
    private bool _sizeHeld;

    private void HoldSize(bool hold)
    {
        if (hold == _sizeHeld) return;
        _sizeHeld = hold;
        SizeToContent = hold ? SizeToContent.Manual : SizeToContent.WidthAndHeight;
        if (!hold) UpdateLayout();
    }

    /// <summary>Brings a hidden bar's size and position up to date (it stays hidden), e.g. to know where it would open.</summary>
    internal void RefreshHeldSize()
    {
        if (!_sizeHeld) return;
        HoldSize(false);
        HoldSize(true);
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
        if (Math.Abs(dip - ShiftDip) < 0.05 && dip != 0) return;
        ShiftDip = dip;
        _slide.X = dip;
        _backdrop.SetShift(dip * VisualTreeHelper.GetDpi(this).DpiScaleX);
        ClickWholeWindow(dip != 0);
    }

    // A layered window lets clicks through wherever it's transparent; this one would take them all, so its region
    // keeps it to the glass. (Not to the glass's exact shape: a region has no soft edges, it would cut off the glass's
    // anti-aliased rim.) While the glass changes shape under the cursor or slides, the whole window takes part.
    private bool _clickWhole;
    private Native.RECT _lastRegion;

    /// <summary>Whether the window's whole area takes clicks, or only the glass where it rests.</summary>
    protected void ClickWholeWindow(bool whole)
    {
        if (whole == _clickWhole) return;
        _clickWhole = whole;
        UpdateClickRegion();
    }

    private void UpdateClickRegion()
    {
        if (Layered || Handle == IntPtr.Zero || !Native.GetWindowRect(Handle, out var w)) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var g = GlassRect;
        var region = _clickWhole || IsSliding
            ? new Native.RECT { Right = w.Right - w.Left, Bottom = w.Bottom - w.Top }
            : new Native.RECT { Left = (int)Math.Floor(g.Left * s), Top = (int)Math.Floor(g.Top * s), Right = (int)Math.Ceiling(g.Right * s), Bottom = (int)Math.Ceiling(g.Bottom * s) };
        if (region.Equals(_lastRegion)) return;
        _lastRegion = region;
        Native.SetWindowRgn(Handle, CreateRectRgn(region.Left, region.Top, region.Right, region.Bottom), false);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    // ---------------- slide in / out ----------------
    // The content and the blur window's visual slide frame by frame. While sliding, the content is rendered once into
    // a bitmap cache at device resolution, so the glass effects (refraction, blur passes, glow) aren't recomputed every
    // frame; nothing inside the bar changes mid-slide (see ScreenSampler, the dock's magnifier) to keep that cache valid.
    // Drawing that cache is the slide's first frame, and takes up to 200 ms over an app keeping the graphics card busy.
    // On a clock that keeps running, the bar stood still and then jumped, losing most of the slide; this clock pauses.

    /// <summary>Longest step the slide takes in one frame: a slow frame pauses it instead of making it jump ahead.</summary>
    private const double MaxSlideStepMs = 1000 / 45.0;
    private readonly System.Diagnostics.Stopwatch _slideClock = new();
    private EventHandler? _slideFrame;
    private bool _cloaked;

    /// <summary>True while a slide animation is running.</summary>
    internal bool IsSliding { get; private set; }

    /// <summary>Slides the bar into view or off the bottom of the screen.</summary>
    internal void Slide(bool shown, double seconds, Action? completed = null)
    {
        if (shown && !CanShow) return;
        StopSlide();
        IsSlidIn = shown;
        if (shown) HoldSize(false);
        // A window that isn't layered shows black until its first frame is drawn: it stays cloaked until then.
        int waitFrames = 0;
        if (shown && !IsVisible)
        {
            if (!Layered)
            {
                SetCloaked(true);
                waitFrames = RevealAfterFrames;
            }
            Show();
        }
        // hidden bars don't sample the screen: take a fresh picture now, before it's cached for the slide
        if (shown && !IsSliding) ScreenSampler.SampleNow(this);

        IsSliding = true;
        UpdateClickRegion();
        if (Content is FrameworkElement root && root.CacheMode == null)
            root.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX) { SnapsToDevicePixels = true };

        double from = _slide.Y, to = shown ? 0 : SlideDistance;
        // a partial slide (reversed midway) takes proportionally less time
        double ms = Math.Max(1, seconds * 1000 * Math.Abs(to - from) / SlideDistance);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double elapsed = 0, last = -1;
        _slideFrame = (_, _) =>
        {
            if (waitFrames > 0)
            {
                if (--waitFrames == 0) SetCloaked(false);
                return;
            }
            if (last < 0) { _slideClock.Restart(); last = 0; }
            double now = _slideClock.Elapsed.TotalMilliseconds;
            elapsed += Math.Min(now - last, MaxSlideStepMs);
            last = now;
            double t = Math.Min(1, elapsed / ms);
            double eased = shown ? 1 - Math.Pow(1 - t, 3) : t * t;
            _slide.Y = from + (to - from) * eased;
            _backdrop.SetOffsetY(_slide.Y * scale);
            if (t < 1) return;
            StopSlide();
            IsSliding = false;
            UpdateClickRegion();
            // back to live rendering, then e.g. hide
            if (Content is FrameworkElement r) r.CacheMode = null;
            completed?.Invoke();
        };
        CompositionTarget.Rendering += _slideFrame;
    }

    private void StopSlide()
    {
        if (_slideFrame == null) return;
        CompositionTarget.Rendering -= _slideFrame;
        _slideFrame = null;
        if (_cloaked) SetCloaked(false);
    }

    private const int RevealAfterFrames = 2;
    private const int DWMWA_CLOAK = 13;

    private void SetCloaked(bool on)
    {
        _cloaked = on;
        int v = on ? 1 : 0;
        Native.DwmSetWindowAttribute(new WindowInteropHelper(this).EnsureHandle(), DWMWA_CLOAK, ref v, sizeof(int));
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
        UpdateClickRegion();
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