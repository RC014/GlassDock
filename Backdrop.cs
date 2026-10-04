using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDock.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace GlassDock;

/// <summary>
/// A click-through window that sits directly under a bar and shows the system's live blur of whatever is
/// behind it, clipped to an anti-aliased rounded rectangle. (Window regions don't clip DWM blur on
/// Windows 11, which produced square corners; a composition clip does.)
/// </summary>
internal sealed class Backdrop : IDisposable
{
    private static Compositor? _compositor;
    private static IntPtr _dispatcherQueueController;
    private static ushort _classAtom;
    private static readonly WndProcDelegate WndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);

    private readonly DesktopWindowTarget _target;
    private readonly SpriteVisual _sprite;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly ContainerVisual _root;
    private Native.RECT _lastRect;
    private Vector3 _glassOffset;
    private float _shift;

    public IntPtr Handle { get; }

    public Backdrop()
    {
        EnsureCompositor();
        EnsureWindowClass();

        Handle = CreateWindowEx(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "GlassDockBackdrop", "", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);

        int v = 1;
        Native.DwmSetWindowAttribute(Handle, DWMWA_USE_HOSTBACKDROPBRUSH, ref v, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_EXCLUDED_FROM_PEEK, ref v, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref v, sizeof(int));
        v = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
        v = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, sizeof(int));

        var c = _compositor!;
        _target = CreateTarget(c, Handle);
        var root = _root = c.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;
        _target.Root = root;

        _geometry = c.CreateRoundedRectangleGeometry();
        _sprite = c.CreateSpriteVisual();
        // With refraction on, the bar paints the refracted background itself (opaque), so no system blur is needed.
        _sprite.Brush = Settings.Current.Refraction ? null : MakeGlassBrush(c, Settings.Current.GlassBlur);
        _sprite.Clip = c.CreateGeometricClip(_geometry);
        root.Children.InsertAtTop(_sprite);
    }

    /// <summary>Matches the bar window's rectangle (physical px) and places the blur at glassPx inside it.</summary>
    public void Update(Native.RECT windowRect, Rect glassPx, double radiusPx)
    {
        if (!windowRect.Equals(_lastRect))
        {
            _lastRect = windowRect;
            SetWindowPos(Handle, IntPtr.Zero, windowRect.Left, windowRect.Top,
                windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
        }
        // Inset slightly so the bar's anti-aliased rim fully covers the blur's edge.
        const float inset = 1f;
        var size = new Vector2((float)Math.Max(0, glassPx.Width - inset * 2), (float)Math.Max(0, glassPx.Height - inset * 2));
        _glassOffset = new Vector3((float)glassPx.X + inset, (float)glassPx.Y + inset, 0);
        _sprite.Offset = _glassOffset + new Vector3(_shift, 0, 0);
        _sprite.Size = size;
        _geometry.Size = size;
        _geometry.CornerRadius = new Vector2((float)Math.Max(0, radiusPx - inset));
    }

    /// <summary>Slides the blur vertically (physical px) in step with the bar's own slide animation.</summary>
    public void SlideTo(double yPx, double seconds, bool easeOut)
    {
        var c = _compositor!;
        // cubic-bezier equivalents of WPF's CubicEase(EaseOut) and QuadraticEase(EaseIn)
        var ease = easeOut
            ? c.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f))
            : c.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.085f), new Vector2(0.68f, 0.53f));
        var anim = c.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, (float)yPx, ease);
        anim.Duration = TimeSpan.FromSeconds(seconds);
        _root.StartAnimation("Offset.Y", anim);
    }

    /// <summary>Shifts the blur sideways (physical px) along with the bar's content.</summary>
    public void SetShift(double px)
    {
        _shift = (float)px;
        _sprite.Offset = _glassOffset + new Vector3(_shift, 0, 0);
    }

    public void SetVisible(bool visible) => ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : Native.SW_HIDE);

    public void Dispose() => DestroyWindow(Handle);

    /// <summary>
    /// Frosted glass uses the system's live blur; clear glass draws nothing here (the bar's own glass layers
    /// sit over the sharp background). The system blur has a fixed strength and can't be blended partially:
    /// DWM stops drawing it when its visual is translucent, and mask brushes don't accept it as a source.
    /// </summary>
    private static CompositionBrush? MakeGlassBrush(Compositor c, double amount) =>
        amount >= 0.5 ? c.CreateHostBackdropBrush() : null;

    // ---------------- setup ----------------

    private static void EnsureCompositor()
    {
        if (_compositor != null) return;
        var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        CreateDispatcherQueueController(options, out _dispatcherQueueController);
        _compositor = new Compositor();
    }

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0) return;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "GlassDockBackdrop",
        };
        _classAtom = RegisterClassEx(ref wc);
    }

    private static DesktopWindowTarget CreateTarget(Compositor compositor, IntPtr hwnd)
    {
        IntPtr unknown = ((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr;
        Guid iid = typeof(ICompositorDesktopInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out IntPtr interopPtr));
        try
        {
            var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(interopPtr);
            interop.CreateDesktopWindowTarget(hwnd, false, out IntPtr targetPtr);
            var target = DesktopWindowTarget.FromAbi(targetPtr);
            Marshal.Release(targetPtr);
            return target;
        }
        finally { Marshal.Release(interopPtr); }
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }

    // ---------------- Win32 ----------------

    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004, SWP_NOOWNERZORDER = 0x0200;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
