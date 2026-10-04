using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GlassDock;

/// <summary>
/// "GlassDock.exe --uninstall", run by Windows' Installed apps › Uninstall for a copy installed with
/// GlassDockSetup.exe: restores the Windows taskbar and removes the shortcuts, startup entry, uninstall entry
/// and installed files (and, if you want, your settings).
/// </summary>
internal static class Uninstaller
{
    /// <summary>Where GlassDockSetup.exe installs GlassDock (per user, no admin rights needed).</summary>
    public static readonly string InstallFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassDock");

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDock";

    public static void Run()
    {
        if (MessageBox.Show("Remove GlassDock from this computer?\n\nThe Windows taskbar will be restored.",
                "Uninstall GlassDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool deleteSettings = MessageBox.Show("Also delete your GlassDock settings and pinned apps?",
                "Uninstall GlassDock", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Stop any running GlassDock and bring the Windows taskbar back.
        foreach (var p in Process.GetProcessesByName("GlassDock"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        TaskbarRescue.Restore();

        TryDelete(() =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("GlassDock", false);
        });
        TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false));
        TryDelete(() => File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GlassDock.lnk")));
        TryDelete(() => File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GlassDock.lnk")));
        if (deleteSettings) TryDelete(() => Directory.Delete(Settings.Folder, true));

        // This exe is inside the install folder and can't delete itself while running: let cmd remove the
        // folder a couple of seconds after we exit.
        if (Directory.Exists(InstallFolder))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallFolder}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }

        MessageBox.Show("GlassDock was uninstalled.", "Uninstall GlassDock", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void TryDelete(Action action)
    {
        try { action(); } catch { }
    }
}
