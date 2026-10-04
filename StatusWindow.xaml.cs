using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GlassDock.Interop;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace GlassDock;

/// <summary>The right-hand bar: tray icons, network/volume/battery and the clock.</summary>
public partial class StatusWindow : GlassWindow
{
    private const byte VK_A = 0x41, VK_N = 0x4E;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _promotedTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private HashSet<string> _promoted = new(StringComparer.OrdinalIgnoreCase);
    private ListCollectionView? _inline, _overflow;
    private int _tick;

    public StatusWindow()
    {
        Resources["Radius"] = new CornerRadius(Settings.Current.CornerRadius);
        InitializeComponent();
        RootGrid.Height = BarHeight;

        SetupTray();
        _flyoutLeaveTimer.Tick += (_, _) => FlyoutLeaveTick();
        OverflowPopup.Opened += Flyout_Opened;
        OverflowPopup.Closed += Flyout_Closed;
        UpdateClock();
        UpdateNetwork();
        UpdateVolume();
        UpdateBattery();

        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            UpdateVolume();
            if (++_tick % 15 == 0) { UpdateBattery(); UpdateNetwork(); }
        };
        _clockTimer.Start();

        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);
    }

    protected override Rect GlassRect => new(0, 0, ActualWidth, Math.Max(0, ActualHeight - Settings.Current.BottomMargin));

    /// <summary>True while the hidden-icons flyout is open.</summary>
    public bool IsFlyoutOpen => OverflowPopup.IsOpen;

    protected override void Reposition()
    {
        var s = Screen;
        Left = Math.Round(s.Width - Settings.Current.SideMargin - ActualWidth);
        Top = Math.Round(s.Height - ActualHeight); // the window reaches the screen edge; the gap is a bottom margin inside it
    }

    // ---------------- tray ----------------

    private static readonly string[] OwnSystemIcons =
    {
        NotificationArea.NETWORK_GUID, NotificationArea.VOLUME_GUID, NotificationArea.POWER_GUID,
    };

    private void SetupTray()
    {
        var area = App.Shell.NotificationArea;
        if (area?.TrayIcons == null) { ChevronButton.Visibility = Visibility.Collapsed; return; }

        _promoted = ReadPromotedIcons();
        _inline = MakeView(area, inline: true);
        _overflow = MakeView(area, inline: false);
        TrayList.ItemsSource = _inline;
        OverflowList.ItemsSource = _overflow;

        area.TrayIcons.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateChevron);
        ((System.Collections.Specialized.INotifyCollectionChanged)_overflow).CollectionChanged += (_, _) => UpdateChevron();
        UpdateChevron();

        // Windows stores the "always show" choice in the registry; pick up changes made in Settings.
        _promotedTimer.Tick += (_, _) =>
        {
            var next = ReadPromotedIcons();
            if (next.SetEquals(_promoted)) return;
            _promoted = next;
            _inline.Refresh();
            _overflow.Refresh();
            UpdateChevron();
        };
        _promotedTimer.Start();
    }

    private ListCollectionView MakeView(NotificationArea area, bool inline)
    {
        var view = new ListCollectionView(area.TrayIcons)
        {
            Filter = o => o is NotifyIcon icon && !icon.IsHidden && !IsOwnSystemIcon(icon) && IsPromoted(icon) == inline,
            IsLiveFiltering = true,
        };
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.Path));
        return view;
    }

    private static bool IsOwnSystemIcon(NotifyIcon icon) =>
        OwnSystemIcons.Any(g => Guid.TryParse(g, out var guid) && icon.GUID == guid);

    private bool IsPromoted(NotifyIcon icon)
    {
        if (icon.IsPinned && icon.GUID != Guid.Empty) return true; // Windows system icons ManagedShell pins by default
        string? path = icon.Path;
        return !string.IsNullOrEmpty(path) && _promoted.Contains(Path.GetFileName(path));
    }

    /// <summary>Executable names whose tray icons are set to "always show" in Windows Settings.</summary>
    private static HashSet<string> ReadPromotedIcons()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return set;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("IsPromoted") is int p && p == 1 && k.GetValue("ExecutablePath") is string exe)
                    set.Add(Path.GetFileName(exe));
            }
        }
        catch { }
        return set;
    }

    private void UpdateChevron()
    {
        bool any = _overflow != null && !_overflow.IsEmpty;
        ChevronButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) OverflowPopup.IsOpen = false;
    }

    // The flyout is opened/closed by hand (StaysOpen=True): the chevron toggles it, and while it is open a
    // low-level mouse hook closes it on any click outside the flyout and the chevron. WPF's own StaysOpen=False
    // handling misfires in a non-activating window and re-opened the flyout on the chevron's second click.
    private IntPtr _mouseHook;
    private Native.LowLevelMouseProc? _mouseProc;

    private void Chevron_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
    }

    private void Flyout_Opened(object? sender, EventArgs e)
    {
        _flyoutLeaveTimer.Start();
        _flyoutOutsideSince = null;
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
    }

    private void Flyout_Closed(object? sender, EventArgs e)
    {
        _flyoutLeaveTimer.Stop();
        if (_mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    // Backup for the click hook (Windows silently drops low-level hooks that are ever slow to answer):
    // the flyout also closes once the cursor has been away from it and the bar for a moment.
    private readonly DispatcherTimer _flyoutLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime? _flyoutOutsideSince;

    private void FlyoutLeaveTick()
    {
        Native.GetCursorPos(out var p);
        if (IsOver(OverflowPopup.Child, p) || ContainsCursor(p)) { _flyoutOutsideSince = null; return; }
        _flyoutOutsideSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _flyoutOutsideSince > TimeSpan.FromMilliseconds(500)) OverflowPopup.IsOpen = false;
    }

    internal override void CloseTransients()
    {
        base.CloseTransients();
        OverflowPopup.IsOpen = false;
        _quickSettings?.Hide();
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
        {
            var pt = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINT>(lParam);
            if (!IsOver(OverflowPopup.Child, pt) && !IsOver(ChevronButton, pt))
                Dispatcher.BeginInvoke(() => OverflowPopup.IsOpen = false);
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>Whether a physical screen point is over an element.</summary>
    private static bool IsOver(UIElement? element, Native.POINT p)
    {
        if (element == null || !element.IsVisible || PresentationSource.FromVisual(element) == null) return false;
        var fe = (FrameworkElement)element;
        var tl = fe.PointToScreen(new Point(0, 0));
        var br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
        return p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y;
    }

    private static NotifyIcon? IconOf(object sender) => (sender as FrameworkElement)?.DataContext as NotifyIcon;

    private void Tray_MouseEnter(object sender, MouseEventArgs e) => IconOf(sender)?.IconMouseEnter(MouseHelper.GetCursorPositionParam());
    private void Tray_MouseLeave(object sender, MouseEventArgs e) => IconOf(sender)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
    private void Tray_MouseMove(object sender, MouseEventArgs e) => IconOf(sender)?.IconMouseMove(MouseHelper.GetCursorPositionParam());

    private void Tray_MouseDown(object sender, MouseButtonEventArgs e)
    {
        IconOf(sender)?.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), (int)Native.GetDoubleClickTime());
        e.Handled = true;
    }

    private void Tray_MouseUp(object sender, MouseButtonEventArgs e)
    {
        IconOf(sender)?.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), (int)Native.GetDoubleClickTime());
        e.Handled = true; // keep the bar menu from opening on right-click
    }

    // ---------------- system indicators ----------------

    // GlassDock's own glass Quick Settings panel, opened from the network/volume/battery button.
    private QuickSettingsWindow? _quickSettings;

    /// <summary>The Quick Settings panel, opening it first (for the GLASSDOCK_DUMP debug renders).</summary>
    internal Window OpenQuickSettingsForDump()
    {
        if (!IsQuickSettingsOpen) System_Click(this, null!);
        return _quickSettings!;
    }

    /// <summary>True while the Quick Settings panel is open (keeps the status bar up).</summary>
    public bool IsQuickSettingsOpen => _quickSettings?.IsVisible == true;

    private void System_Click(object sender, MouseButtonEventArgs e)
    {
        if (_quickSettings == null)
        {
            _quickSettings = new QuickSettingsWindow();
            _quickSettings.IsOverOpener = p => IsOver(SystemButton, p);
        }
        var glass = new Rect(Left, Top, ActualWidth, Math.Max(0, ActualHeight - Settings.Current.BottomMargin));
        _quickSettings.Toggle(this, glass);
    }

    private void System_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Audio.Adjust(e.Delta > 0 ? 0.02f : -0.02f);
        App.ShowVolumeOsd();
        UpdateVolume();
    }

    private void System_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        Audio.ToggleMute();
        UpdateVolume();
    }

    private void UpdateVolume()
    {
        var v = Audio.Get();
        if (v == null) { VolGlyph.Text = Glyph(0xE74F); return; }
        var (level, muted) = v.Value;
        VolGlyph.Text = Glyph(muted || level < 0.01f ? 0xE74F : level < 0.34f ? 0xE993 : level < 0.67f ? 0xE994 : 0xE995);
        SystemButton.ToolTip = muted ? "Muted" : $"Volume {Math.Round(level * 100)}%";
    }

    private void UpdateNetwork()
    {
        bool wifi = false, wired = false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (!nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any))) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true; else wired = true;
            }
        }
        catch { }
        NetGlyph.Text = Glyph(wifi || !wired ? 0xE701 : 0xE839);
        NetGlyph.Opacity = wifi || wired ? 1 : 0.35;
    }

    private void UpdateBattery()
    {
        if (!Native.GetSystemPowerStatus(out var ps) || ps.BatteryFlag == 128 || ps.BatteryFlag == 255 || ps.BatteryLifePercent > 100)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        BatteryPanel.Visibility = Visibility.Visible;
        int pct = ps.BatteryLifePercent;
        bool charging = ps.ACLineStatus == 1;
        int step = (int)Math.Round(pct / 10.0);
        BatGlyph.Text = Glyph((charging ? 0xEBAB : 0xEBA0) + step);
        BatText.Text = pct + "%";
    }

    /// <summary>A Segoe Fluent Icons glyph by code point.</summary>
    private static string Glyph(int codePoint) => ((char)codePoint).ToString();

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var c = CultureInfo.CurrentCulture;
        TimeText.Text = now.ToString(Settings.Current.ShowSeconds ? c.DateTimeFormat.LongTimePattern : c.DateTimeFormat.ShortTimePattern, c);
        DateText.Visibility = Settings.Current.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        DateText.Text = now.ToString("ddd d MMM", c);
        ClockButton.ToolTip = now.ToString("D", c);
    }

    private void Clock_Click(object sender, MouseButtonEventArgs e) => Native.SendWinCombo(VK_N);

    private void Bar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        DockWindow.OpenMenu(DockWindow.BuildBarMenu(), this);
    }
}
