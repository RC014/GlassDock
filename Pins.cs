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
            // A desktop app's entry also knows its .exe: match its windows by that too (many apps don't tag their
            // windows with the entry's id).
            if (Shell.GetLinkTarget(path) is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) item.ExePath = t;
        }
        else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var link = Shell.ReadLink(path);
            if (link?.TargetPath is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) item.ExePath = t;
            item.AppUserModelId = link?.AppUserModelId;
            item.Name = Path.GetFileNameWithoutExtension(path);
            // a launcher's arguments often name the program it starts ("Update.exe --processStart Discord.exe")
            if (link?.Arguments is { Length: > 0 } args)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(args, @"[^\s""\\/]+\.exe", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    item.LaunchHints.Add(m.Value);
        }
        else
        {
            item.ExePath = path;
            if (IsExplorer(path))
            {
                // explorer.exe pinned as a file: give it File Explorer's identity so it matches its windows.
                item.AppUserModelId = ExplorerAumid;
                item.Name = "File Explorer";
            }
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
            item.Name = IsExplorer(w.WinFileName) ? "File Explorer" // its file description says "Windows Explorer"
                      : !string.IsNullOrWhiteSpace(w.WinFileDescription) ? w.WinFileDescription
                      : !string.IsNullOrEmpty(w.WinFileName) ? Path.GetFileNameWithoutExtension(w.WinFileName) : w.Title;
            item.Icon = (!string.IsNullOrEmpty(w.WinFileName) ? Shell.GetIcon(w.WinFileName, IconPx) : null) ?? w.Icon;
        }
        return item;
    }

    /// <summary>
    /// Whether a window belongs to a pinned item, so it shows on the pinned icon rather than as a second icon. Tried in
    /// order: same app id; same .exe (even for windows Windows reports as modern apps, like Windows 11's File
    /// Explorer); an .exe learned for this pin (see <see cref="Settings.PinAliases"/>); the same program installed in
    /// another version folder; and the program a pinned launcher starts.
    /// </summary>
    public static bool Matches(DockItem pin, ApplicationWindow w) => Matches(pin, w.AppUserModelID, w.WinFileName, w.WinFileDescription);

    internal static bool Matches(DockItem pin, string? windowAumid, string? exe, string? description)
    {
        if (pin.AppUserModelId != null && string.Equals(pin.AppUserModelId, windowAumid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrEmpty(exe)) return false;
        if (pin.ExePath != null && SamePath(pin.ExePath, exe)) return true;
        if (pin.PinPath != null && Settings.Current.PinAliases.TryGetValue(exe.ToLowerInvariant(), out var aliasOf)
            && string.Equals(aliasOf, pin.PinPath, StringComparison.OrdinalIgnoreCase))
            return true;
        if (pin.ExePath == null || IsExplorer(exe)) return false;

        string pinFile = Path.GetFileName(pin.ExePath), runFile = Path.GetFileName(exe);
        if (string.Equals(pinFile, runFile, StringComparison.OrdinalIgnoreCase))
        {
            // the same program, updated into a new version folder (...\App\app-1.0\App.exe vs ...\App\app-1.1\App.exe)
            if (InVersionFolders(pin.ExePath, exe)) return true;
            // or the same product installed elsewhere
            var a = ProductOf(pin.ExePath);
            var b = ProductOf(exe);
            if (a.Product.Length > 0 && a == b) return true;
        }

        // A pinned launcher (Update.exe, a "Launcher.exe"...) starting the real program from inside its own folder:
        // only when the shortcut names that program or the names agree, so e.g. games don't merge into a pinned Steam.
        string? pinDir = Path.GetDirectoryName(pin.ExePath);
        if (pinDir != null && !IsBroadFolder(pinDir) && IsUnder(exe, pinDir)
            && (pin.LaunchHints.Contains(runFile) || NamesAgree(pin.Name, exe, description)))
            return true;
        return false;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\'); }
        catch { return path; }
    }

    private static bool IsUnder(string file, string folder)
    {
        string f = Normalize(file), d = Normalize(folder) + "\\";
        return f.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both in sibling folders with a version-like name (containing a digit) under the same parent.</summary>
    private static bool InVersionFolders(string a, string b)
    {
        string? da = Path.GetDirectoryName(Normalize(a)), db = Path.GetDirectoryName(Normalize(b));
        if (da == null || db == null) return false;
        string? pa = Path.GetDirectoryName(da), pb = Path.GetDirectoryName(db);
        return pa != null && string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(da).Any(char.IsDigit) && Path.GetFileName(db).Any(char.IsDigit)
            && !IsBroadFolder(pa);
    }

    /// <summary>Folders too general to say two programs belong together (Windows, Program Files, AppData...).</summary>
    private static bool IsBroadFolder(string folder)
    {
        string f = Normalize(folder);
        return BroadFolders.Value.Contains(f) || Path.GetPathRoot(f)?.TrimEnd('\\') == f;
    }

    private static readonly Lazy<System.Collections.Generic.HashSet<string>> BroadFolders = new(() =>
    {
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var special in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData,
        })
        {
            string s = Environment.GetFolderPath(special);
            if (s.Length > 0) set.Add(Normalize(s));
        }
        set.Add(Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")));
        return set;
    });

    /// <summary>The pin's name and the program's (file description, product name or file name) are the same word.</summary>
    private static bool NamesAgree(string pinName, string exe, string? description)
    {
        string pin = Simplify(pinName);
        if (pin.Length < 3) return false;
        foreach (var candidate in new[] { description, ProductOf(exe).Product, Path.GetFileNameWithoutExtension(exe) })
        {
            string c = Simplify(candidate);
            if (c.Length < 3) continue;
            if (c == pin || (Math.Min(c.Length, pin.Length) >= 4 && (c.Contains(pin) || pin.Contains(c)))) return true;
        }
        return false;
    }

    private static string Simplify(string? s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static readonly System.Collections.Generic.Dictionary<string, (string Product, string Company)> Products = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's product and company names from its version info (cached; empty if it has none).</summary>
    private static (string Product, string Company) ProductOf(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return ("", "");
        if (Products.TryGetValue(exe, out var cached)) return cached;
        (string, string) result = ("", "");
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            result = ((v.ProductName ?? "").Trim(), (v.CompanyName ?? "").Trim());
        }
        catch { }
        Products[exe] = result;
        return result;
    }

    /// <summary>Whether this program may be learned as belonging to a pin (not Windows' own shell processes).</summary>
    public static bool CanLearn(ApplicationWindow w) =>
        !w.IsUWP && !string.IsNullOrEmpty(w.WinFileName) && !IsExplorer(w.WinFileName)
        && !Normalize(w.WinFileName).StartsWith(Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The path to store in settings when pinning a running app.</summary>
    public static string? PinPathFor(DockItem running)
    {
        // Prefer the app's Windows identity when it has one Windows knows (Start menu entry): that gives the
        // proper name and icon and matches its windows reliably. Otherwise pin the exe.
        if (running.AppUserModelId != null && Shell.GetDisplayName(AppsFolder + running.AppUserModelId) != null)
            return AppsFolder + running.AppUserModelId;
        if (running.ExePath != null && IsExplorer(running.ExePath)) return AppsFolder + ExplorerAumid;
        return running.ExePath;
    }

    private const string ExplorerAumid = "Microsoft.Windows.Explorer";

    private static bool IsExplorer(string? exe) =>
        exe != null && string.Equals(exe, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);

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
