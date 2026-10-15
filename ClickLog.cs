using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using GlassDock.Interop;
using ManagedShell.WindowsTasks;

namespace GlassDock;

/// <summary>
/// dock.log in the settings folder: what happened when an app's dock icon or window preview was clicked - the app's
/// windows before (marked active, minimised, maximised...), what GlassDock did, and the window a moment later. For bug
/// reports about windows that don't come back, or come back wrong. No window titles are recorded (they can hold
/// chat names, document names and so on), only the program, the window class and the state.
/// </summary>
internal static class ClickLog
{
    private const int MaxLines = 60;

    /// <param name="target">The window GlassDock acted on, whose state is noted again a moment later (or null).</param>
    public static void Note(string app, IEnumerable<ApplicationWindow> windows, string action, ApplicationWindow? target)
    {
        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {app}: {string.Join(" | ", windows.Select(Describe))}  ->  {action}");
        if (target == null) return;
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}    then: {Describe(target)}; in front: {Foreground()}");
        };
        later.Start();
    }

    /// <summary>Notes a line of its own (e.g. the Start menu's state), and optionally another a moment later.</summary>
    public static void Line(string text, Func<string>? later = null)
    {
        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}");
        if (later == null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}    then: {later()}");
        };
        timer.Start();
    }

    private static string Describe(ApplicationWindow w)
    {
        try
        {
            IntPtr h = w.Handle;
            var flags = new List<string> { w.State.ToString() };
            if (IsIconic(h)) flags.Add("minimised");
            if (IsZoomed(h)) flags.Add("maximised");
            if (!IsWindowVisible(h)) flags.Add("hidden");
            if (Native.GetForegroundWindow() == h) flags.Add("in front");
            Native.GetWindowRect(h, out var r);
            int style = Native.GetWindowLong(h, -16 /* GWL_STYLE */);
            return $"{string.Join(" ", flags)} ({ClassOf(h)}, style 0x{style:X8}, rect {r.Left},{r.Top},{r.Right},{r.Bottom})";
        }
        catch (Exception ex) { return "? " + ex.GetType().Name; }
    }

    private static string Foreground()
    {
        IntPtr h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return "nothing";
        GetWindowThreadProcessId(h, out uint pid);
        string process = "?";
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }
        Native.GetWindowRect(h, out var r);
        return $"{process} ({ClassOf(h)}, style 0x{Native.GetWindowLong(h, -16):X8}, maximised {IsZoomed(h)}, rect {r.Left},{r.Top},{r.Right},{r.Bottom})";
    }

    private static string ClassOf(IntPtr h)
    {
        var cls = new StringBuilder(64);
        Native.GetClassName(h, cls, cls.Capacity);
        return cls.ToString();
    }

    private static void Append(string line)
    {
        try
        {
            string path = Path.Combine(Settings.Folder, "dock.log");
            var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>()) { line };
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
