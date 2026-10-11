using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// The drawn part of a glass bar. Bottom to top: the refracted background (a live copy of the screen
/// behind the bar run through <see cref="LensEffect"/>: a flat sheet of glass, bent in a band along the rim and
/// blurred over the middle), a light tint, a body gradient, a soft inner edge glow, a top sheen, a diagonal shine and a thin
/// specular rim. Every layer uses the "Radius" resource for its corners; brushes come from <see cref="Theme"/>.
/// </summary>
public sealed class GlassSurface : Grid
{
    private readonly Grid _lensHost = new() { IsHitTestVisible = false };
    // The sampled image is painted as a background brush: a brush has no size of its own, so the image can't
    // feed back into the layout (an Image would grow the bar to the bitmap's pixel size, and so on).
    private readonly Border _lensImage = new();
    private readonly ImageBrush _lensBrush = new() { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
    private readonly LensEffect _lens = new();
    // Two passes of a separable Gaussian blur after the refraction (nested elements: innermost effect runs first).
    private readonly Border _blurH = new(), _blurV = new();
    private readonly GlassBlurEffect _blurHEffect = new(), _blurVEffect = new();
    private readonly Grid _glowHost = new() { IsHitTestVisible = false };

    /// <summary>Blur radius at the centre line, in DIPs, at 100% blur.</summary>
    private const double MaxBlurDip = 18;

    // screen sampling buffers
    private IntPtr _memDc, _dib, _bits;
    private int _w, _h;
    private byte[] _prev = Array.Empty<byte>(), _curr = Array.Empty<byte>();
    private WriteableBitmap? _bitmap;

    public GlassSurface()
    {
        if (Settings.Current.Refraction)
        {
            _lens.Strength = Settings.Current.RefractionStrength;
            _lensImage.Effect = _lens;
            RenderOptions.SetBitmapScalingMode(_lensBrush, BitmapScalingMode.Linear);
            _lensImage.Background = _lensBrush;

            UIElement top = _lensImage;
            if (Settings.Current.RefractionBlur > 0)
            {
                _blurH.Child = _lensImage;
                _blurH.Effect = _blurHEffect;
                _blurV.Child = _blurH;
                _blurV.Effect = _blurVEffect;
                top = _blurV;
            }
            _lensHost.Children.Add(top);
            Children.Add(_lensHost);
            Loaded += (_, _) => { if (_source == null) ScreenSampler.Register(this); };
            // Fresh sample the moment the bar is shown, so it never opens with the picture from last time.
            IsVisibleChanged += (_, e) =>
            {
                if (!(bool)e.NewValue) return;
                if (_source == null) ScreenSampler.SampleNow(this);
                else OnSourceSampled();
            };
            Unloaded += (_, _) => { ScreenSampler.Unregister(this); FreeBuffers(); };
        }

        Add("GlassTint", null, 0, 0, hitTest: true);
        Add("GlassFill", null, 0, 0, hitTest: true);

        // Inner glow: a stroke along the edge, blurred so it fades gradually inward (no visible bands),
        // clipped to the glass so nothing spills outside.
        var glow = new Border
        {
            BorderThickness = new Thickness(3),
            Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Quality, KernelType = KernelType.Gaussian },
        };
        glow.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        glow.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");
        _glowHost.Children.Add(glow);
        Children.Add(_glowHost);
        // Re-aim the sampled background right after every resize (same frame as the wave's stretch).
        SizeChanged += (_, _) => { UpdateClip(); UpdateViewbox(); };

        Add("GlassSheen", null, 0, 1, hitTest: false);
        Add("GlassShine", null, 0, 1, hitTest: false);
        Add(null, "GlassRim", 1, 0, hitTest: false);
    }

    /// <summary>Scales the width of the refracting band (1 = normal; smaller for big panels such as the Start menu).</summary>
    public double BandScale { get; set; } = 1;

    // ---------------- panels made of separate pieces of glass ----------------
    // A panel window (Start menu, Quick Settings) is filled by a backdrop: the screen behind the window, shown as
    // it is, so the window itself disappears. The panel's pieces of glass (GlassTile) all refract that same
    // capture, so there's still only one screen copy per panel.

    private bool _isBackdrop;
    private GlassSurface? _source;
    private UIElement? _anchor;

    /// <summary>Raised after this surface took a new screen sample (pieces of glass sharing it re-aim then).</summary>
    internal event Action? Sampled;

    /// <summary>Turns this surface into a panel's backdrop: the captured screen as it is, no lens, tint or light.</summary>
    internal void MakeBackdrop()
    {
        _isBackdrop = true;
        Children.Clear();
        if (!Settings.Current.Refraction) return;
        _blurV.Child = null;
        _blurH.Child = null;
        _lensImage.Effect = null;
        _lensHost.Children.Clear();
        _lensHost.Children.Add(_lensImage);
        Children.Add(_lensHost);
    }

    /// <summary>
    /// Makes this piece of glass refract <paramref name="backdrop"/>'s screen capture instead of taking its own.
    /// Its place in that capture is measured ignoring <paramref name="anchor"/>'s render transform (the panel's
    /// open/close slide), so during the animation the glass travels with its picture instead of re-aiming.
    /// </summary>
    internal void ShareFrom(GlassSurface backdrop, UIElement anchor)
    {
        _source = backdrop;
        _anchor = anchor;
        ScreenSampler.Unregister(this);
        backdrop.Sampled += OnSourceSampled;
        LayoutUpdated += (_, _) => UpdateViewbox();
        // Hovering or typing over the glass then doesn't re-run its shaders.
        CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        OnSourceSampled();
    }

    private void OnSourceSampled()
    {
        if (_source?._bitmap is { } picture && !ReferenceEquals(_lensBrush.ImageSource, picture)) _lensBrush.ImageSource = picture;
        UpdateViewbox();
    }

    private void UpdateClip()
    {
        if (_isBackdrop) { _lensHost.Clip = null; return; }
        double r = TryFindResource("Radius") is CornerRadius cr ? cr.TopLeft : 0;
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        clip.Freeze();
        _glowHost.Clip = clip;
        _lensHost.Clip = clip;
        if (ActualHeight > 0)
        {
            double aspect = ActualWidth / ActualHeight;
            _lens.Aspect = _blurHEffect.Aspect = _blurVEffect.Aspect = aspect;
            // The rounded bevel along the rim is about as wide as the glass's corner rounding (at least 16 DIPs, at
            // most half the short side, so a pill-shaped bar is curved right across); RefractionEdge (bevel width)
            // and BandScale narrow it. Its rounded rectangle uses at least the bevel width as corner radius, so the
            // bending turns smoothly round the corners.
            double minDim = Math.Min(ActualWidth, ActualHeight);
            // (and at least 15% of the short side, so big panels get an edge in proportion, like the bars)
            double band = Math.Clamp(Math.Max(Math.Max(r, 16), minDim * 0.15) * Settings.Current.RefractionEdge * BandScale, 2, minDim / 2);
            double radius = Math.Min(Math.Max(r, band), minDim / 2);
            _lens.Band = _blurHEffect.Band = _blurVEffect.Band = band / ActualHeight;
            _lens.Radius = _blurHEffect.Radius = _blurVEffect.Radius = radius / ActualHeight;
            // blur radius at the centre line (fading to none at the rim), per pass direction, in uv units
            double blurDip = Settings.Current.RefractionBlur * MaxBlurDip;
            _blurHEffect.Direction = new Point(blurDip / Math.Max(1, ActualWidth), 0);
            _blurVEffect.Direction = new Point(0, blurDip / ActualHeight);
        }
    }

    private void Add(string? background, string? border, double thickness, double margin, bool hitTest)
    {
        var b = new Border { Margin = new Thickness(margin), BorderThickness = new Thickness(thickness), IsHitTestVisible = hitTest };
        b.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        if (background != null) b.SetResourceReference(Border.BackgroundProperty, background);
        if (border != null) b.SetResourceReference(Border.BorderBrushProperty, border);
        Children.Add(b);
    }

    // ---------------- screen sampling ----------------

    internal bool NeedsSample => IsVisible && ActualWidth >= 2 && ActualHeight >= 2 && PresentationSource.FromVisual(this) != null;

    /// <summary>No new pictures while the bar slides: the last one travels with the glass and the bar isn't redrawn.</summary>
    internal bool IsSliding => IsAnimating || Window.GetWindow(this) is GlassWindow { IsSliding: true };

    /// <summary>Set while a panel animates (PanelAnimation): no new screen samples, so the cached glass stays cached.</summary>
    internal bool IsAnimating { get; set; }

    /// <summary>Copies the screen area currently under this surface (physical px) into the lens image.</summary>
    internal void Sample(IntPtr screenDc)
    {
        // Capture the window's whole area where it rests (even while it slides). It already has room for the
        // dock's magnification stretch, so the brush can always show the exact slice under the glass
        // (UpdateViewbox) without running out of image or stretching; while sliding, the picture moves
        // rigidly with the window.
        // Bars use their resting rectangle; other windows (the preview panel) where they currently are.
        var window = Window.GetWindow(this);
        if (window == null) return;
        Native.RECT rest;
        if (window is GlassWindow bar) rest = bar.RestRectPx;
        else if (!Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out rest)) return;
        int x = rest.Left, y = rest.Top, w = rest.Right - rest.Left, h = rest.Bottom - rest.Top;
        if (w < 2 || h < 2) return;

        if (w != _w || h != _h || _bitmap == null)
        {
            FreeBuffers();
            _memDc = CreateCompatibleDC(screenDc);
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            _dib = CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            SelectObject(_memDc, _dib);
            _w = w; _h = h;
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            _lensBrush.ImageSource = _bitmap;
        }

        BitBlt(_memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY);

        // Only push a new frame when the background actually changed: every update redraws the whole bar.
        int len = w * h * 4;
        if (_prev.Length != len) _prev = new byte[len];
        if (_curr.Length != len) _curr = new byte[len];
        Marshal.Copy(_bits, _curr, 0, len);
        if (TestBackground.Value is { } test) test.CopyTo(_curr, x, y, w, h);
        if (!_curr.AsSpan().SequenceEqual(_prev))
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _curr, w * 4, 0);
            (_prev, _curr) = (_curr, _prev);
        }
        UpdateViewbox();
        Sampled?.Invoke();
    }

    /// <summary>
    /// Debug aid for tuning the glass: GLASSDOCK_TESTBG=&lt;png&gt; makes every glass refract that picture (stretched
    /// over the primary screen) instead of what's really behind it.
    /// </summary>
    private static readonly Lazy<TestPicture?> TestBackground = new(() =>
        Environment.GetEnvironmentVariable("GLASSDOCK_TESTBG") is { Length: > 0 } path && System.IO.File.Exists(path) ? new TestPicture(path) : null);

    private sealed class TestPicture
    {
        private readonly byte[] _pixels;
        private readonly int _w, _h;

        public TestPicture(string path)
        {
            _w = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            _h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var src = new BitmapImage(new Uri(path));
            var scaled = new TransformedBitmap(src, new ScaleTransform((double)_w / src.PixelWidth, (double)_h / src.PixelHeight));
            var bgr = new FormatConvertedBitmap(scaled, PixelFormats.Bgr32, null, 0);
            _pixels = new byte[_w * _h * 4];
            bgr.CopyPixels(_pixels, _w * 4, 0);
        }

        /// <summary>Copies the screen area (x, y, w, h) of the picture into a w×h BGRA buffer.</summary>
        public void CopyTo(byte[] target, int x, int y, int w, int h)
        {
            int x0 = Math.Clamp(x, 0, _w), x1 = Math.Clamp(x + w, 0, _w);
            for (int row = 0; row < h; row++)
            {
                int sy = Math.Clamp(y + row, 0, _h - 1);
                if (x1 > x0) Buffer.BlockCopy(_pixels, (sy * _w + x0) * 4, target, (row * w + (x0 - x)) * 4, (x1 - x0) * 4);
            }
        }
    }

    /// <summary>Points the brush at the part of the captured window area that is under the glass. The position is
    /// taken relative to the window's content root, which ignores the root's transform: vertically that's
    /// deliberate (while sliding, the picture travels with the glass instead of racing through the lens), but
    /// the sideways shift (the media bubble following the dock's stretch) is added back, so a bubble that moves
    /// sideways shows what is actually behind it. Called every frame while visible.</summary>
    internal void UpdateViewbox()
    {
        if ((_source?._bitmap ?? _bitmap) == null || Window.GetWindow(this) is not { Content: Visual root } window || PresentationSource.FromVisual(this) == null) return;
        if (_anchor != null && !_anchor.IsAncestorOf(this)) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double shift = window is GlassWindow gw ? gw.ShiftDip : 0;
        Point tl, br;
        if (_anchor is Visual anchor)
        {
            // position in the window, leaving out the anchor's own (animated) render transform
            var toAnchor = TransformToAncestor(anchor);
            var at = LayoutOffset(anchor, root);
            tl = toAnchor.Transform(new Point(0, 0)) + at;
            br = toAnchor.Transform(new Point(ActualWidth, ActualHeight)) + at;
        }
        else
        {
            var toWindow = TransformToAncestor(root);
            tl = toWindow.Transform(new Point(0, 0));
            br = toWindow.Transform(new Point(ActualWidth, ActualHeight));
        }
        var box = new Rect((tl.X + shift) * s, tl.Y * s, Math.Max(1, (br.X - tl.X) * s), Math.Max(1, (br.Y - tl.Y) * s));
        if (box != _lensBrush.Viewbox) _lensBrush.Viewbox = box;
    }

    /// <summary>Where <paramref name="v"/> sits inside <paramref name="root"/> by layout alone (no render transforms).</summary>
    private static Vector LayoutOffset(Visual v, Visual root)
    {
        var offset = new Vector();
        for (DependencyObject? d = v; d != null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is Visual visual) offset += VisualTreeHelper.GetOffset(visual);
        return offset;
    }

    private void FreeBuffers()
    {
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _bitmap = null;
        _w = _h = 0;
        _prev = Array.Empty<byte>();
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
