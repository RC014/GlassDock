using System;
using System.Runtime.InteropServices;
using System.Text;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// Registers a bottom strip with the shell so maximised windows stop above the dock,
/// and reports when a full-screen app (game, video) takes over so the bars can hide.
/// </summary>
internal sealed class AppBar : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly uint _callbackMsg;
    private bool _registered;
    private int _heightPx;

    public event Action<bool>? FullScreenChanged;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _callbackMsg = Native.RegisterWindowMessage("GlassDock.AppBarMessage");
    }

    public void Reserve(int heightPx)
    {
        _heightPx = heightPx;
        if (!_registered)
        {
            var abd = NewData();
            abd.uCallbackMessage = _callbackMsg;
            Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
            _registered = true;
        }
        SetPos();
    }

    private void SetPos()
    {
        var abd = NewData();
        abd.uEdge = Native.ABE_BOTTOM;
        int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        abd.rc = new Native.RECT { Left = 0, Right = sw, Top = sh - _heightPx, Bottom = sh };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - _heightPx;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
    }

    public void Release()
    {
        if (!_registered) return;
        var abd = NewData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        _registered = false;
    }

    /// <summary>Forget the registration (e.g. after Explorer restarted) so the next Reserve re-registers.</summary>
    public void Reset() => _registered = false;

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != _callbackMsg) return false;
        switch (wParam.ToInt32())
        {
            case Native.ABN_POSCHANGED:
                if (_registered) SetPos();
                break;
            case Native.ABN_FULLSCREENAPP:
                FullScreenChanged?.Invoke(lParam != IntPtr.Zero && AutoHide.ForegroundIsFullScreen());
                break;
        }
        return true;
    }

    private Native.APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };

    public void Dispose() => Release();
}
