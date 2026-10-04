using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace GlassDock;

/// <summary>
/// Low-level keyboard hook on its own thread (so a busy UI thread never delays typing). It takes over:
/// <list type="bullet">
/// <item>a lone press of the Windows key, which opens the glass Start menu. Windows-key shortcuts keep working: the
/// Windows key is held back until the next key shows whether it's a shortcut, then replayed in front of that key.</item>
/// <item>the volume keys, which change the volume in 2% steps and show the glass indicator instead of Windows' pop-up.</item>
/// </list>
/// Keys GlassDock injects itself are ignored, so replayed shortcuts pass straight through.
/// </summary>
internal static class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_L = 0x4C;
    private const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static LowLevelKeyboardProc? _proc; // kept alive while hooked
    private static Dispatcher _ui = null!;

    // Windows-key state (only touched on the hook thread)
    private static int _winKey;          // VK of the Windows key currently held back, 0 if none
    private static bool _winReplayed;    // a shortcut followed: Windows already got a (replayed) Win-down
    private static bool _winUsed;        // a shortcut we handled ourselves (Win+L) followed: swallow the Win-up too
    private static uint _usedAt;         // event time of that shortcut

    /// <summary>Fired on the UI thread when the Windows key is pressed and released on its own.</summary>
    public static event Action? WindowsKeyPressed;
    /// <summary>Fired on the UI thread after a volume key changed the volume.</summary>
    public static event Action? VolumeChanged;

    public static void Start()
    {
        if (_thread != null) return;
        _ui = Dispatcher.CurrentDispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlassDock keyboard hook" };
        _thread.Start();
    }

    public static void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    private static void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { App.Log(new InvalidOperationException("Keyboard hook failed: " + Marshal.GetLastWin32Error())); return; }
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            int vk = (int)k.vkCode;
            var s = Settings.Current;

            // ---- volume keys ----
            if (s.GlassVolumeIndicator && vk is VK_VOLUME_MUTE or VK_VOLUME_DOWN or VK_VOLUME_UP)
            {
                if (down) _ui.BeginInvoke(() =>
                {
                    if (vk == VK_VOLUME_MUTE) Interop.Audio.ToggleMute();
                    else Interop.Audio.Adjust(vk == VK_VOLUME_UP ? 0.02f : -0.02f);
                    VolumeChanged?.Invoke();
                });
                return (IntPtr)1;
            }

            // ---- Windows key ----
            if (vk is VK_LWIN or VK_RWIN)
            {
                if (_winKey == 0 && !s.WindowsKeyOpensGlassStart) return CallNextHookEx(_hook, code, wParam, lParam);
                if (down)
                {
                    // After Win+L the key-up happens on the lock screen, where we don't see it: start over.
                    bool stale = _winUsed && unchecked(k.time - _usedAt) > 1500;
                    if (_winKey == 0 || stale) { _winKey = vk; _winReplayed = false; _winUsed = false; }
                    return _winReplayed ? CallNextHookEx(_hook, code, wParam, lParam) : (IntPtr)1; // auto-repeat while held
                }
                if (up && vk == _winKey)
                {
                    bool replayed = _winReplayed, used = _winUsed;
                    _winKey = 0;
                    _winReplayed = _winUsed = false;
                    if (replayed) return CallNextHookEx(_hook, code, wParam, lParam); // matches the replayed Win-down
                    if (!used) _ui.BeginInvoke(() => WindowsKeyPressed?.Invoke());
                    return (IntPtr)1;
                }
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            // ---- another key while the Windows key is held back: it's a shortcut ----
            if (_winKey != 0 && down && !_winReplayed)
            {
                if (vk == VK_L && !_winUsed)
                {
                    // Windows ignores an injected Win+L, so lock directly.
                    _winUsed = true;
                    _usedAt = k.time;
                    LockWorkStation();
                    return (IntPtr)1;
                }
                if (_winUsed) return CallNextHookEx(_hook, code, wParam, lParam);
                // Replay Win-down, then this key, so Windows sees the shortcut in the right order. Keys after this
                // (and this key's key-up) pass through normally while Windows considers Win held.
                _winReplayed = true;
                keybd_event((byte)_winKey, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event((byte)vk, (byte)k.scanCode, (k.flags & 1) != 0 ? KEYEVENTF_EXTENDEDKEY : 0, UIntPtr.Zero);
                return (IntPtr)1;
            }
        }
        catch { }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
