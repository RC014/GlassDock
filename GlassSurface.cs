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
/// behind the bar run through <see cref="LensEffect"/>, magnified and softened towards the centre, clear at the
/// rim), a light tint, a body gradient, a soft inner edge glow, a top sheen, a diagonal shine and a thin
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
            // RefractionEdge = share of the rim-to-centre distance over which the effect builds up
            double edge = 1 / (0.5 * Settings.Current.RefractionEdge);
            _lens.Edge = _blurHEffect.Edge = _blurVEffect.Edge = edge;
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
            Loaded += (_, _) => ScreenSampler.Register(this);
            // Fresh sample the moment the bar is shown, so it never opens with the picture from last time.
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ScreenSampler.SampleNow(this); };
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

    private void UpdateClip()
    {
        double r = TryFindResource("Radius") is CornerRadius cr ? cr.TopLeft : 0;
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        clip.Freeze();
        _glowHost.Clip = clip;
        _lensHost.Clip = clip;
        if (ActualHeight > 0)
        {
            double aspect = ActualWidth / ActualHeight;
            _lens.Aspect = _blurHEffect.Aspect = _blurVEffect.Aspect = aspect;
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
    internal bool IsSliding => Window.GetWindow(this) is GlassWindow { IsSliding: true };

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
        if (!_curr.AsSpan().SequenceEqual(_prev))
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _curr, w * 4, 0);
            (_prev, _curr) = (_curr, _prev);
        }
        UpdateViewbox();
    }

    /// <summary>Points the brush at the part of the captured window area that is under the glass. The position is
    /// taken relative to the window's content root, which ignores the root's transform: vertically that's
    /// deliberate (while sliding, the picture travels with the glass instead of racing through the lens), but
    /// the sideways shift (the media bubble following the dock's stretch) is added back, so a bubble that moves
    /// sideways shows what is actually behind it. Called every frame while visible.</summary>
    internal void UpdateViewbox()
    {
        if (_bitmap == null || Window.GetWindow(this) is not { Content: Visual root } window || PresentationSource.FromVisual(this) == null) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double shift = window is GlassWindow gw ? gw.ShiftDip : 0;
        var toWindow = TransformToAncestor(root);
        var tl = toWindow.Transform(new Point(0, 0));
        var br = toWindow.Transform(new Point(ActualWidth, ActualHeight));
        var box = new Rect((tl.X + shift) * s, tl.Y * s, Math.Max(1, (br.X - tl.X) * s), Math.Max(1, (br.Y - tl.Y) * s));
        if (box != _lensBrush.Viewbox) _lensBrush.Viewbox = box;
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
