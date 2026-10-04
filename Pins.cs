using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GlassDock.Interop;
using ManagedShell.WindowsTasks;

namespace GlassDock;

/// <summary>Creating dock items for pinned paths / running windows, matching them, and launching apps.</summary>
internal static class Pins
{
    private const string AppsFolder = @"shell:AppsFolder\";

    /// <summary>Icons are rendered at 2x so they stay sharp on high-DPI screens.</summary>
    private static int IconPx => (int)Math.Max(96, Settings.Current.IconSize * Math.Max(1, Settings.Current.Magnification) * 2);

    /// <summary>On first run, copy the user's current Windows taskbar pins into the dock.</summary>
    public static void ImportTaskbarPinsOnce()
    {
        var s = Settings.Current;
        if (s.ImportedTaskbarPins) return;
        s.ImportedTaskbarPins = true;
        try
        {
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            Directory.CreateDirectory(Settings.PinnedFolder);
            if (Directory.Exists(src))
            {
                foreach (var lnk in Directory.GetFiles(src, "*.lnk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(Settings.PinnedFolder, Path.GetFileName(lnk));
                    File.Copy(lnk, dest, true);
                    if (!s.Pinned.Contains(dest, StringComparer.OrdinalIgnoreCase)) s.Pinned.Add(dest);
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }

        if (s.Pinned.Count == 0)
            s.Pinned.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        Settings.Save();
    }

    public static DockItem CreatePinned(string path)
    {
        var item = new DockItem("pin:" + path) { PinPath = path, IsPinned = true };

        if (path.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
        {
            item.AppUserModelId = path.Substring(AppsFolder.Length);
        }
        else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var link = Shell.ReadLink(path);
            if (link?.TargetPath is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) item.ExePath = t;
            item.AppUserModelId = link?.AppUserModelId;
            item.Name = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            item.ExePath = path;
        }

        if (string.IsNullOrEmpty(item.Name))
            item.Name = (item.ExePath != null ? ExeDescription(item.ExePath) : null) ?? Shell.GetDisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
        item.Icon = Shell.GetIcon(path, IconPx);
        return item;
    }

    public static string KeyFor(ApplicationWindow w)
    {
        if (w.IsUWP && !string.IsNullOrEmpty(w.AppUserModelID)) return "aumid:" + w.AppUserModelID;
        if (!string.IsNullOrEmpty(w.WinFileName)) return "exe:" + w.WinFileName.ToLowerInvariant();
        return "hwnd:" + w.Handle;
    }

    public static DockItem CreateRunning(ApplicationWindow w)
    {
        var item = new DockItem(KeyFor(w));
        if (w.IsUWP && !string.IsNullOrEmpty(w.AppUserModelID))
        {
            item.AppUserModelId = w.AppUserModelID;
            string p = AppsFolder + w.AppUserModelID;
            item.Name = Shell.GetDisplayName(p) ?? w.Title;
            item.Icon = Shell.GetIcon(p, IconPx) ?? w.Icon;
        }
        else
        {
            item.ExePath = w.WinFileName;
            item.AppUserModelId = string.IsNullOrEmpty(w.AppUserModelID) ? null : w.AppUserModelID;
            item.Name = !string.IsNullOrWhiteSpace(w.WinFileDescription) ? w.WinFileDescription
                      : !string.IsNullOrEmpty(w.WinFileName) ? Path.GetFileNameWithoutExtension(w.WinFileName) : w.Title;
            item.Icon = (!string.IsNullOrEmpty(w.WinFileName) ? Shell.GetIcon(w.WinFileName, IconPx) : null) ?? w.Icon;
        }
        return item;
    }

    public static bool Matches(DockItem pin, ApplicationWindow w)
    {
        if (pin.AppUserModelId != null && string.Equals(pin.AppUserModelId, w.AppUserModelID, StringComparison.OrdinalIgnoreCase))
            return true;
        return pin.ExePath != null && !w.IsUWP && string.Equals(pin.ExePath, w.WinFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path to store in settings when pinning a running app.</summary>
    public static string? PinPathFor(DockItem running)
    {
        if (running.ExePath == null && running.AppUserModelId != null) return AppsFolder + running.AppUserModelId;
        if (running.ExePath != null && running.ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && running.AppUserModelId != null)
            return AppsFolder + running.AppUserModelId;
        return running.ExePath;
    }

    public static void Launch(DockItem item)
    {
        try
        {
            string? target = item.PinPath ?? item.ExePath ?? (item.AppUserModelId != null ? AppsFolder + item.AppUserModelId : null);
            if (target == null) return;
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = SafeDir(target) });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static string SafeDir(string path)
    {
        try { return Path.GetDirectoryName(path) ?? ""; } catch { return ""; }
    }

    private static string? ExeDescription(string exe)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        }
        catch { return null; }
    }
}
