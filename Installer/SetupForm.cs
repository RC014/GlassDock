using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GlassDockSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // "/S" (or --silent): install or update without any window and start GlassDock.
            if (args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    SetupForm.Install(desktopShortcut: false);
                    SetupForm.Finish(launch: true);
                    return 0;
                }
                catch { return 1; }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }
    }

    /// <summary>
    /// One-page installer: copies the embedded GlassDock.exe to %LOCALAPPDATA%\Programs\GlassDock and registers
    /// Start menu / desktop shortcuts, start with Windows and an entry in Settings › Apps › Installed apps.
    /// Everything is per user, so no administrator rights are needed. Running it over an existing install updates
    /// it and keeps the user's settings.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private static readonly string InstallFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassDock");
        private static readonly string InstalledExe = Path.Combine(InstallFolder, "GlassDock.exe");
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDock";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static readonly string Version = Application.ProductVersion.Split('+')[0];
        private readonly string _version = Version;
        private readonly bool _isUpdate = File.Exists(InstalledExe);

        private readonly Label _body;
        private readonly CheckBox _desktop, _launch;
        private readonly Button _install, _cancel;
        private readonly ProgressBar _progress;

        public SetupForm()
        {
            Text = "GlassDock Setup";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            // Fixed width, height fitted to the contents (see the end), so it fits at any display scaling.
            int textWidth = LogicalToDeviceUnits(470);

            var title = new Label { Text = "GlassDock " + _version, Font = new Font("Segoe UI Semibold", 18f), AutoSize = true };
            var subtitle = new Label
            {
                Text = "A glass dock and status bar for Windows 11", ForeColor = Color.DimGray, AutoSize = true,
                Margin = new Padding(3, 0, 3, 14),
            };
            _body = new Label
            {
                AutoSize = true, MaximumSize = new Size(textWidth, 0), Margin = new Padding(3, 0, 3, 14),
                Text = (_isUpdate ? "GlassDock is already installed. Setup will update it and keep your settings."
                                  : "GlassDock will be installed for your user account (no administrator rights needed).")
                       + "\n\nLocation: " + InstallFolder
                       + "\n\nIt replaces the Windows taskbar while it runs and starts when you sign in; both can be changed in its settings.",
            };
            _desktop = new CheckBox { Text = "Create a desktop shortcut", AutoSize = true };
            _launch = new CheckBox { Text = "Start GlassDock when setup finishes", AutoSize = true, Checked = true };
            _progress = new ProgressBar { Width = textWidth, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(3, 10, 3, 0) };

            var content = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
                Padding = new Padding(LogicalToDeviceUnits(24), LogicalToDeviceUnits(18), LogicalToDeviceUnits(24), LogicalToDeviceUnits(8)),
            };
            content.Controls.AddRange(new Control[] { title, subtitle, _body, _desktop, _launch, _progress });

            var buttonSize = new Size(LogicalToDeviceUnits(100), LogicalToDeviceUnits(32));
            _install = new Button { Text = _isUpdate ? "Update" : "Install", Size = buttonSize };
            _cancel = new Button { Text = "Cancel", Size = buttonSize };
            _install.Click += async (_, __) => await InstallAsync();
            _cancel.Click += (_, __) => Close();
            AcceptButton = _install;
            CancelButton = _cancel;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top, Padding = new Padding(LogicalToDeviceUnits(20), 0, LogicalToDeviceUnits(20), LogicalToDeviceUnits(16)),
            };
            buttons.Controls.AddRange(new Control[] { _cancel, _install });

            // Docked Top controls stack in reverse order of adding.
            Controls.Add(buttons);
            Controls.Add(content);
            ClientSize = new Size(textWidth + content.Padding.Horizontal + LogicalToDeviceUnits(8),
                                  content.PreferredSize.Height + buttons.PreferredSize.Height + LogicalToDeviceUnits(10));
        }

        private async Task InstallAsync()
        {
            _install.Enabled = _cancel.Enabled = _desktop.Enabled = _launch.Enabled = false;
            _progress.Visible = true;
            bool desktop = _desktop.Checked, launch = _launch.Checked;
            try
            {
                await Task.Run(() => Install(desktop));
            }
            catch (Exception ex)
            {
                _progress.Visible = false;
                MessageBox.Show(this, "Setup couldn't install GlassDock:\n\n" + ex.Message, "GlassDock Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _install.Enabled = _cancel.Enabled = _desktop.Enabled = _launch.Enabled = true;
                return;
            }

            Finish(launch);

            _progress.Visible = false;
            _body.Text = _isUpdate ? "GlassDock was updated." : "GlassDock is installed.\n\nYou'll find it in the Start menu, and it can be removed from Settings › Apps › Installed apps.";
            _desktop.Visible = _launch.Visible = false;
            _cancel.Visible = false;
            _install.Text = "Finish";
            _install.Enabled = true;
            _install.Click += (_, __) => Close();
        }

        /// <summary>Brings the Windows taskbar back after closing an old copy, then starts the new one (which hides it again).</summary>
        internal static void Finish(bool launch)
        {
            Process.Start(new ProcessStartInfo(InstalledExe, "--restore") { UseShellExecute = false })?.WaitForExit(5000);
            if (launch) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallFolder });
        }

        internal static void Install(bool desktopShortcut)
        {
            // Close a running copy so its exe can be replaced.
            foreach (var p in Process.GetProcessesByName("GlassDock"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }

            Directory.CreateDirectory(InstallFolder);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassDock.exe"))
            using (var file = File.Create(InstalledExe))
            {
                if (payload == null) throw new InvalidOperationException("The installer is damaged (GlassDock.exe is missing).");
                payload.CopyTo(file);
            }

            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GlassDock.lnk"));
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GlassDock.lnk");
            if (desktopShortcut) CreateShortcut(desktopLink);

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.SetValue("GlassDock", "\"" + InstalledExe + "\"");

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "GlassDock");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Stefan Mandrescu");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallFolder);
                key.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                key.SetValue("URLInfoAbout", "https://github.com/RC014/GlassDock");
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void CreateShortcut(string path)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallFolder;
            link.Description = "GlassDock";
            link.Save();
        }
    }
}