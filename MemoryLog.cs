using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlassDock;

/// <summary>
/// memory.log in the settings folder: GlassDock's memory and object counts, and how much memory the PC has left, every
/// ten minutes. For bug reports about the PC slowing down after hours of work: it shows whether GlassDock is growing,
/// and which number grows. It records no window titles or anything else about what you're doing.
/// </summary>
internal static class MemoryLog
{
    private const int MaxLines = 1000; // about a week of running
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMinutes(10) };

    public static void Start()
    {
        Timer.Tick += (_, _) => Note();
        Timer.Start();
        Note();
    }

    private static void Note()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref status);
            string capture = !Settings.Current.Refraction ? "off" : GpuCapture.Current != null ? "GPU" : "BitBlt";
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  committed {p.PrivateMemorySize64 >> 20} MB, in RAM {p.WorkingSet64 >> 20} MB, " +
                          $".NET {GC.GetTotalMemory(false) >> 20} MB, handles {p.HandleCount}, threads {p.Threads.Count}, " +
                          $"GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}, screen capture {capture}; " +
                          $"PC: RAM {status.dwMemoryLoad}% used, {status.ullAvailPageFile >> 20} MB of {status.ullTotalPageFile >> 20} MB commit left";
            string path = Path.Combine(Settings.Folder, "memory.log");
            var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>()) { line };
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
