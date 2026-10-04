using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>An app in the Start menu (from Windows' own Start list).</summary>
public sealed class StartApp
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public StartApp(string name, string id) { Name = name; Id = id; }

    public string Name { get; }
    /// <summary>Id in the shell's Applications folder; opened as shell:AppsFolder\&lt;Id&gt;.</summary>
    public string Id { get; }
    public string ParsingName => @"shell:AppsFolder\" + Id;

    /// <summary>Loaded on first use, so only the icons actually on screen are read.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _iconLoaded = true; _icon = Shell.GetIcon(ParsingName, 64); }
            return _icon;
        }
    }

    public void Launch()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", ParsingName) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }
}

/// <summary>A recently opened file (from Windows' Recent items).</summary>
public sealed class StartRecent
{
    public StartRecent(string name, string linkPath, DateTime when)
    {
        Name = name;
        LinkPath = linkPath;
        When = when;
        Icon = Shell.GetIcon(linkPath, 48);
    }

    public string Name { get; }
    public string LinkPath { get; }
    public DateTime When { get; }
    public ImageSource? Icon { get; }

    public string Ago
    {
        get
        {
            var d = DateTime.Now - When;
            if (d.TotalMinutes < 1) return "Just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
            return When.ToString("d MMM");
        }
    }

    public void Open()
    {
        try { Process.Start(new ProcessStartInfo(LinkPath) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }
}

/// <summary>Data for the Start menu: all apps, recent files and the signed-in user.</summary>
internal static class StartData
{
    private static Task<List<StartApp>>? _apps;

    /// <summary>All apps, alphabetical. Read once (on a background STA thread) and cached.</summary>
    public static Task<List<StartApp>> GetAppsAsync() => _apps ??= RunSta(() =>
        Shell.EnumerateStartApps()
            .Where(a => !a.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.Id).Select(g => g.First())
            .Select(a => new StartApp(a.Name, a.Id))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    /// <summary>Forget the cached app list (e.g. after installing something) so the next open re-reads it.</summary>
    public static void Refresh() => _apps = null;

    public static List<StartRecent> GetRecent(int count)
    {
        var list = new List<StartRecent>();
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime))
            {
                var target = Shell.ReadLink(f.FullName)?.TargetPath;
                if (target == null || !File.Exists(target)) continue; // folders and deleted files aren't shown
                list.Add(new StartRecent(Path.GetFileName(target), f.FullName, f.LastWriteTime));
                if (list.Count >= count) break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return list;
    }

    /// <summary>The signed-in user's display name and account picture.</summary>
    public static async Task<(string Name, ImageSource? Picture)> GetUserAsync()
    {
        string name = Environment.UserName;
        ImageSource? picture = null;
        try
        {
            var users = await Windows.System.User.FindAllAsync();
            var user = users.FirstOrDefault();
            if (user != null)
            {
                if (await user.GetPropertyAsync(Windows.System.KnownUserProperties.DisplayName) is string display && display.Length > 0) name = display;
                var stream = await user.GetPictureAsync(Windows.System.UserPictureSize.Size208x208);
                if (stream != null)
                {
                    using var s = await stream.OpenReadAsync();
                    using var net = s.AsStream();
                    var ms = new MemoryStream();
                    await net.CopyToAsync(ms);
                    ms.Position = 0;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    picture = img;
                }
            }
        }
        catch { }
        return (name, picture);
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
