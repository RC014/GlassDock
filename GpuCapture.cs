using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// The screen behind the glass, taken through Windows' own screen capture on the graphics card instead of GDI's
/// BitBlt. A BitBlt of the screen costs 4-8 ms however small the area (it waits for the desktop to be composed),
/// and every piece of glass needed its own, so with a panel open the copies alone left room for about 30 frames a
/// second. Here Windows hands over a new frame only when the screen changed, on a worker thread; the areas the glass
/// needs are copied out of it there, and the UI thread just picks up its part of that copy.
/// <para>
/// Captures the primary screen (where the bars and panels live), without the mouse pointer and without the
/// capture border. GlassDock's own windows are excluded from capture (WDA_EXCLUDEFROMCAPTURE) as with BitBlt.
/// If any of this isn't available (older Windows, no graphics card, remote session), <see cref="Current"/>
/// stays null and the glass keeps using BitBlt.
/// </para>
/// </summary>
internal sealed class GpuCapture
{
    /// <summary>The running capture, or null (then use BitBlt).</summary>
    public static GpuCapture? Current { get; private set; }

    private readonly object _lock = new();
    private readonly IntPtr _device, _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _size;

    // _latest: the last frame (on the graphics card), kept so a newly needed area can be copied out any time;
    // _staging: where areas are copied for the CPU to read; _snapshot: those areas, CPU side (screen-sized, same layout).
    private IntPtr _latest, _staging;
    private int _w, _h;
    private byte[] _snapshot = Array.Empty<byte>();
    private Native.RECT[] _wanted = Array.Empty<Native.RECT>();
    private Native.RECT[] _covered = Array.Empty<Native.RECT>();
    private int _version;

    /// <summary>Goes up by one with every new frame of the screen.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Starts capturing in the background; <see cref="Current"/> is set once it runs.</summary>
    public static async void Start()
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return;
            // Without the yellow "being captured" border (always allowed for desktop apps; refused, we don't capture).
            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            if (access != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed) return;
            Current = new GpuCapture();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private unsafe GpuCapture()
    {
        Check(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out _device, out _, out _context));
        // The frame pool uses the device from its own threads.
        var iidMultithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        if (Marshal.QueryInterface(_context, in iidMultithread, out var mt) == 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(mt, 5))(mt, 1); // SetMultithreadProtected(TRUE)
            Marshal.Release(mt);
        }
        var iidDxgiDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        Check(Marshal.QueryInterface(_device, in iidDxgiDevice, out var dxgi));
        try
        {
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { Marshal.Release(dxgi); }

        _item = ItemForPrimaryScreen();
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _session.IsBorderRequired = false;
        _pool.FrameArrived += (pool, _) => OnFrame(pool);
        _session.StartCapture();
    }

    /// <summary>The areas (screen px) the glass on screen needs: these are copied out of every new frame.</summary>
    public void SetWanted(List<Native.RECT> areas)
    {
        lock (_lock)
        {
            if (areas.Count == _wanted.Length && SameAs(areas, _wanted)) return;
            _wanted = areas.ToArray();
        }
    }

    /// <summary>
    /// Copies the screen area (x, y, w, h in screen px) into <paramref name="target"/> (w×h, 4 bytes a pixel) and
    /// returns the frame's <see cref="Version"/>; -1 if there's no frame yet. Parts beyond the screen are left as they were.
    /// </summary>
    public int CopyTo(byte[] target, int x, int y, int w, int h)
    {
        lock (_lock)
        {
            if (_latest == IntPtr.Zero) return -1;
            var area = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            // An area that isn't in the copy yet (glass that just appeared): take it from the last frame now.
            if (!Covers(_covered, Clamp(area)))
            {
                var wanted = new List<Native.RECT>(_wanted) { area };
                _wanted = wanted.ToArray();
                try { if (!Refresh()) return -1; }
                catch (Exception ex) { App.Log(ex); Stop(); return -1; }
            }
            int x0 = Math.Clamp(x, 0, _w), x1 = Math.Clamp(x + w, 0, _w);
            int y0 = Math.Clamp(y, 0, _h), y1 = Math.Clamp(y + h, 0, _h);
            if (x1 > x0)
                for (int row = y0; row < y1; row++)
                    Buffer.BlockCopy(_snapshot, (row * _w + x0) * 4, target, ((row - y) * w + (x0 - x)) * 4, (x1 - x0) * 4);
            return _version;
        }
    }

    // ---------------- worker side ----------------

    private unsafe void OnFrame(Direct3D11CaptureFramePool pool)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null) return;
            lock (_lock)
            {
                if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
                {
                    // the screen's resolution changed: new buffers from the next frame on
                    _size = frame.ContentSize;
                    _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    FreeTextures();
                    return;
                }
                IntPtr texture = TextureOf(frame.Surface);
                try
                {
                    if (_latest == IntPtr.Zero) CreateTextures(texture);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, 47))(_context, _latest, texture); // CopyResource
                }
                finally { Marshal.Release(texture); }
                if (Refresh()) Interlocked.Increment(ref _version);
            }
        }
        catch (Exception ex)
        {
            // e.g. the graphics driver restarted: fall back to BitBlt
            App.Log(ex);
            Stop();
        }
    }

    /// <summary>Copies the wanted areas of the last frame into the CPU-side snapshot. Call with the lock held.</summary>
    private unsafe bool Refresh()
    {
        if (_latest == IntPtr.Zero) return false;
        var areas = new List<Native.RECT>(_wanted.Length);
        foreach (var a in _wanted)
        {
            var c = Clamp(a);
            if (c.Right > c.Left && c.Bottom > c.Top) areas.Add(c);
        }
        if (areas.Count > 0)
        {
            var copyRegion = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(_context, 46);
            foreach (var a in areas)
            {
                var box = new Box { Left = (uint)a.Left, Top = (uint)a.Top, Front = 0, Right = (uint)a.Right, Bottom = (uint)a.Bottom, Back = 1 };
                copyRegion(_context, _staging, 0, (uint)a.Left, (uint)a.Top, 0, _latest, 0, &box);
            }
            Mapped mapped;
            var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Slot(_context, 14);
            Check(map(_context, _staging, 0, D3D11_MAP_READ, 0, &mapped));
            try
            {
                fixed (byte* snap = _snapshot)
                    foreach (var a in areas)
                    {
                        int bytes = (a.Right - a.Left) * 4;
                        for (int row = a.Top; row < a.Bottom; row++)
                            Buffer.MemoryCopy((byte*)mapped.Data + (long)row * mapped.RowPitch + a.Left * 4, snap + ((long)row * _w + a.Left) * 4, bytes, bytes);
                    }
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, 15))(_context, _staging, 0); } // Unmap
        }
        _covered = areas.ToArray();
        return true;
    }

    private unsafe void CreateTextures(IntPtr frameTexture)
    {
        TextureDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, void>)Slot(frameTexture, 10))(frameTexture, &desc); // GetDesc
        var create = (delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)Slot(_device, 5); // CreateTexture2D
        var latest = new TextureDesc
        {
            Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
            SampleCount = 1, Usage = D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_SHADER_RESOURCE,
        };
        var staging = latest;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.BindFlags = 0;
        staging.CpuAccessFlags = D3D11_CPU_ACCESS_READ;
        IntPtr a, b;
        Check(create(_device, &latest, IntPtr.Zero, &a));
        _latest = a;
        Check(create(_device, &staging, IntPtr.Zero, &b));
        _staging = b;
        _w = (int)desc.Width;
        _h = (int)desc.Height;
        _snapshot = GC.AllocateUninitializedArray<byte>(_w * _h * 4);
        _covered = Array.Empty<Native.RECT>();
    }

    private void FreeTextures()
    {
        if (_latest != IntPtr.Zero) { Marshal.Release(_latest); _latest = IntPtr.Zero; }
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        _covered = Array.Empty<Native.RECT>();
    }

    private void Stop()
    {
        Current = null;
        try { _session.Dispose(); _pool.Dispose(); } catch { }
        lock (_lock) FreeTextures();
    }

    // ---------------- helpers ----------------

    private Native.RECT Clamp(Native.RECT r) => new()
    {
        Left = Math.Clamp(r.Left, 0, _w), Top = Math.Clamp(r.Top, 0, _h),
        Right = Math.Clamp(r.Right, 0, _w), Bottom = Math.Clamp(r.Bottom, 0, _h),
    };

    private static bool Covers(Native.RECT[] areas, Native.RECT r)
    {
        if (r.Right <= r.Left || r.Bottom <= r.Top) return true;
        foreach (var a in areas)
            if (a.Left <= r.Left && a.Top <= r.Top && a.Right >= r.Right && a.Bottom >= r.Bottom) return true;
        return false;
    }

    private static bool SameAs(List<Native.RECT> a, Native.RECT[] b)
    {
        for (int i = 0; i < b.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static unsafe GraphicsCaptureItem ItemForPrimaryScreen()
    {
        IntPtr monitor = MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(WindowsCreateString(cls, cls.Length, out var name));
        IntPtr interop;
        try
        {
            var iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
            Check(RoGetActivationFactory(name, ref iidInterop, out interop));
        }
        finally { WindowsDeleteString(name); }
        try
        {
            var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            IntPtr item;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 4))(interop, monitor, &iidItem, &item)); // CreateForMonitor
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(interop); }
    }

    /// <summary>The ID3D11Texture2D behind a captured frame (released by the caller).</summary>
    private static unsafe IntPtr TextureOf(IDirect3DSurface surface)
    {
        IntPtr unknown = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"); // IDirect3DDxgiInterfaceAccess
            Check(Marshal.QueryInterface(unknown, in iidAccess, out var access));
            try
            {
                var iidTexture = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); // ID3D11Texture2D
                IntPtr texture;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3))(access, &iidTexture, &texture)); // GetInterface
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>Method number <paramref name="index"/> of a COM object's function table.</summary>
    private static unsafe void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D11_USAGE_DEFAULT = 0, D3D11_USAGE_STAGING = 3;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const int D3D11_MAP_READ = 1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount,
        uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
}
