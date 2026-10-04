using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using GlassDock.Interop;
using ManagedShell;
using ManagedShell.Common.Enums;
using Microsoft.Win32;

namespace GlassDock;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _cleanedUp;

    internal static ShellManager Shell { get; private set; } = null!;
    internal static DockWindow? Dock { get; private set; }
    internal static StatusWindow? Status { get; private set; }
    internal static MediaWindow? Media { get; private set; }

    /// <summary>Raised when a full-screen app starts (true) or ends (false).</summary>
    internal static event Action<bool>? FullScreenChanged;
    internal static void RaiseFullScreen(bool fs) => FullScreenChanged?.Invoke(fs);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
        {
            TaskbarRescue.Restore();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "GlassDock.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { Log(a.ExceptionObject as Exception); Cleanup(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        SystemEvents.SessionEnding += (_, _) => Cleanup();

        Settings.Load();
        // On a new computer GlassDock starts with Windows by default (it can be turned off in settings).
        if (Settings.IsFirstRun) StartsWithWindows = true;
        Theme.Refresh();
        Pins.ImportTaskbarPinsOnce();

        if (Settings.Current.HideWindowsTaskbar) TaskbarRescue.RememberOriginalState();

        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.TaskIconSize = IconSize.Large;
        config.EnableTrayService = true;
        config.AutoStartTrayService = true;
        Shell = new ShellManager(config);

        if (Settings.Current.HideWindowsTaskbar) Shell.ExplorerHelper.HideExplorerTaskbar = true;

        Dock = new DockWindow();
        Status = new StatusWindow();
        Dock.Show();
        Status.Show();

        // The media bubble shows itself (next to the dock) once something is playing.
        if (Settings.Current.ShowMediaPlayer) Media = new MediaWindow();

        // Debug aid: with refraction on, the bars are excluded from screen capture, so GLASSDOCK_DUMP=<prefix>
        // renders each bar (effects included) to <prefix>_<bar>_<n>.png once a second for a few seconds.
        if (Environment.GetEnvironmentVariable("GLASSDOCK_DUMP") is { Length: > 0 } dumpPrefix)
        {
            int n = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (_, _) =>
            {
                if (++n > 6) { t.Stop(); return; }
                foreach (var (name, w) in new (string, Window?)[] { ("dock", Dock), ("media", Media), ("status", Status), ("preview", Dock?.PreviewPanel) })
                    if (w is { IsVisible: true }) DumpWindow(w, $"{dumpPrefix}_{name}_{n}.png");
            };
            t.Start();
        }

        if (Settings.Current.AutoHide)
        {
            // Each bar opens on its own: the dock from the bottom edge under it, the status bar from the
            // bottom edge of the right half of the screen (outside the dock's zone).
            const double dockPad = 24;
            var dock = Dock;
            var status = Status;
            var media = Media;
            bool InDockZone(int x) => dock.GlassSpanContains(x, dockPad) || (media is { CanShow: true } && media.GlassSpanContains(x, dockPad));
            var dockGroup = media != null ? new GlassWindow[] { dock, media } : new GlassWindow[] { dock };
            _dockHide = new AutoHide(dockGroup, p => InDockZone(p.X), () => dock.HasOpenMenu || dock.IsDragging || dock.IsPreviewOpen);
            _statusHide = new AutoHide(new GlassWindow[] { status },
                p => p.X >= Native.GetSystemMetrics(Native.SM_CXSCREEN) / 2 && !InDockZone(p.X),
                () => status.HasOpenMenu || status.IsFlyoutOpen);
        }
        else
        {
            FullScreenChanged += fs =>
            {
                foreach (GlassWindow w in new GlassWindow[] { Dock, Status })
                    if (fs) w.Hide(); else w.Show();
            };
        }
    }

    private static AutoHide? _dockHide, _statusHide;

    private static void DumpWindow(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    internal static void Quit()
    {
        Cleanup();
        Current.Shutdown();
    }

    internal static void Restart()
    {
        Cleanup();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Current.Shutdown();
    }

    private static void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try { Dock?.ReleaseScreenSpace(); } catch { }
        try
        {
            if (Shell != null)
            {
                Shell.ExplorerHelper.HideExplorerTaskbar = false;
                Shell.Dispose();
            }
        }
        catch (Exception ex) { Log(ex); }
        if (Settings.Current.HideWindowsTaskbar) TaskbarRescue.Restore();
    }

    internal static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("GlassDock") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("GlassDock", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("GlassDock", false);
        }
    }

    internal static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}