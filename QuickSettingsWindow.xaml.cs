using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Interop;
using Windows.Devices.Radios;

namespace GlassDock;

/// <summary>
/// GlassDock's own Quick Settings, on the same glass as the bars: now playing, Wi-Fi, Bluetooth, airplane mode,
/// accessibility, energy saver, live captions, brightness, volume and battery. Opens above the status bar from
/// its network/volume/battery button; closes when you click elsewhere.
/// </summary>
public partial class QuickSettingsWindow : Window
{
    private IntPtr _hwnd;
    private Radio? _wifi, _bluetooth;
    private Tile _wifiTile = null!, _btTile = null!, _airplaneTile = null!, _saverTile = null!, _captionsTile = null!;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _updating; // true while we set slider values ourselves

    private bool _seeThrough; // the gaps between the pieces of glass show the screen (GlassTile.MakeSeeThrough)

    // click-outside-to-close (same approach as the hidden-icons flyout)
    private IntPtr _mouseHook;
    private Native.LowLevelMouseProc? _mouseProc;

    public QuickSettingsWindow()
    {
        InitializeComponent();
        _anim = new PanelAnimation(this, Body, Glass);
        Resources["QsAccent"] = new SolidColorBrush(AccentColor());
        if (!Settings.Current.Refraction) Glass.Visibility = Visibility.Collapsed;
        BuildTiles();
        Glass.MakeBackdrop();
        GlassTile.ConnectAll(Glass, Body);
        // A click in a gap between the pieces of glass is a click on the desktop: close, like a click outside.
        PreviewMouseDown += (_, e) => { if ((_seeThrough || Settings.Current.Refraction) && !GlassTile.IsOnTile(e.OriginalSource as DependencyObject)) Hide(); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);
            if (Settings.Current.Refraction) Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            _seeThrough = GlassTile.MakeSeeThrough(this, _hwnd, Glass);
            // The window itself is invisible (see-through, or with refraction a backdrop of the screen behind it):
            // no rounding, border or shadow. (Only without either is it a panel of its own.)
            int round = _seeThrough || Settings.Current.Refraction ? Native.DWMWCP_DONOTROUND : 2 /* DWMWCP_ROUND */;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int none = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        };

        MediaController.Instance.Changed += UpdateMedia;
        _poll.Tick += (_, _) => { if (_anim.IsAnimating) return; UpdateVolume(); UpdateBattery(); UpdateToggles(); };
        _brightnessDebounce.Tick += (_, _) => { _brightnessDebounce.Stop(); SystemControls.SetBrightness((int)BrightnessSlider.Value); };
        BrightnessSlider.ValueChanged += (_, _) => { if (!_updating) { _brightnessDebounce.Stop(); _brightnessDebounce.Start(); } };
        VolumeSlider.ValueChanged += (_, _) => { if (!_updating) { Audio.SetLevel((float)(VolumeSlider.Value / 100)); UpdateVolumeGlyph(); } };
        IsVisibleChanged += (_, _) => { if (IsVisible) StartWatching(); else StopWatching(); };
    }

    // ---------------- open / close ----------------

    /// <summary>Opens (or closes, if already open) the panel above the given bar, right-aligned with it.</summary>
    internal void Toggle(Window bar, Rect barGlassScreenDip)
    {
        if (IsVisible && !_anim.IsClosing) { Hide(); return; }
        _ = RefreshAsync();
        // Size it before it shows, so it can rise straight into its final place.
        var root = (FrameworkElement)Content;
        root.Measure(new Size(Width, double.PositiveInfinity));
        Left = Math.Round(barGlassScreenDip.Right - Width);
        _anim.Show(Math.Round(barGlassScreenDip.Top - root.DesiredSize.Height - 10));
        ScreenSampler.SampleNow(Glass);
    }

    /// <summary>Draws the panel once off-screen (it never takes focus), so the first real open doesn't stall.</summary>
    internal async void Prewarm()
    {
        try
        {
            if (IsVisible) return;
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Bottom + 200; // below the screen
            base.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); // after it rendered
            if (!_anim.IsAnimating) base.Hide();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private readonly PanelAnimation _anim;

    /// <summary>Closes the panel with its animation (hides <see cref="Window.Hide"/> on purpose: every close animates).</summary>
    public new void Hide() => _anim.Hide();

    /// <summary>Whether a physical screen point is over the panel.</summary>
    internal bool ContainsScreenPoint(Native.POINT p) =>
        IsVisible && Native.GetWindowRect(_hwnd, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>Set by the status bar so a click on its own button toggles instead of closing-then-reopening.</summary>
    internal Func<Native.POINT, bool>? IsOverOpener { get; set; }

    private void StartWatching()
    {
        _poll.Start();
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
    }

    private void StopWatching()
    {
        _poll.Stop();
        if (_mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
        {
            var pt = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINT>(lParam);
            if (!ContainsScreenPoint(pt) && IsOverOpener?.Invoke(pt) != true)
                Dispatcher.BeginInvoke(Hide);
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    // ---------------- state ----------------

    private async Task RefreshAsync()
    {
        UpdateMedia();
        UpdateVolume();
        UpdateBattery();
        int? brightness = await Task.Run(SystemControls.GetBrightness); // WMI: slow, keep it off the UI thread
        // Changing the contents mid-animation would make it redraw the whole panel then: apply the results once it's open.
        await _anim.WhenIdleAsync();
        BrightnessRow.Visibility = brightness == null ? Visibility.Collapsed : Visibility.Visible;
        if (brightness != null) { _updating = true; BrightnessSlider.Value = brightness.Value; _updating = false; }

        _wifi ??= await SystemControls.GetRadioAsync(RadioKind.WiFi);
        _bluetooth ??= await SystemControls.GetRadioAsync(RadioKind.Bluetooth);
        await _anim.WhenIdleAsync();
        UpdateToggles();
        string? device = await SystemControls.ConnectedBluetoothDeviceAsync();
        await _anim.WhenIdleAsync();
        _btTile.Label = device ?? "Bluetooth";
    }

    private void UpdateToggles()
    {
        _wifiTile.IsOn = _wifi?.State == RadioState.On;
        _wifiTile.Enabled = _wifi != null;
        _wifiTile.Label = (_wifiTile.IsOn ? SystemControls.WifiNetworkName() : null) ?? "Wi-Fi";
        _btTile.IsOn = _bluetooth?.State == RadioState.On;
        _btTile.Enabled = _bluetooth != null;
        _airplaneTile.IsOn = SystemControls.IsAirplaneModeOn() == true;
        _saverTile.IsOn = SystemControls.IsEnergySaverOn();
        _captionsTile.IsOn = SystemControls.IsLiveCaptionsOn();
    }

    private void UpdateMedia()
    {
        var m = MediaController.Instance;
        if (!m.HasMedia)
        {
            MediaTitle.Text = "Not playing";
            MediaArtist.Text = "";
            MediaArt.Source = MediaAppIcon.Source = null;
            MediaPlayGlyph.Text = "";
            return;
        }
        MediaAppName.Text = m.AppName;
        MediaAppIcon.Source = m.AppIcon;
        MediaTitle.Text = m.Title;
        MediaArtist.Text = m.Artist;
        MediaArt.Source = m.Art;
        MediaPlayGlyph.Text = m.IsPlaying ? "" : "";
    }

    private void UpdateVolume()
    {
        if (VolumeSlider.IsMouseCaptureWithin) return; // don't fight the user's drag
        var v = Audio.Get();
        _updating = true;
        VolumeSlider.Value = v == null ? 0 : Math.Round(v.Value.Level * 100);
        _updating = false;
        UpdateVolumeGlyph();
    }

    private void UpdateVolumeGlyph()
    {
        var v = Audio.Get();
        bool muted = v == null || v.Value.Muted || v.Value.Level < 0.01f;
        double level = v?.Level ?? 0;
        VolumeGlyph.Text = ((char)(muted ? 0xE74F : level < 0.34 ? 0xE993 : level < 0.67 ? 0xE994 : 0xE995)).ToString();
    }

    private void UpdateBattery()
    {
        var b = SystemControls.Battery();
        BatteryInfo.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b == null) return;
        int step = (int)Math.Round(b.Value.Percent / 10.0);
        BatteryGlyph.Text = ((char)((b.Value.Charging ? 0xEBAB : 0xEBA0) + step)).ToString();
        BatteryText.Text = b.Value.Percent + "%";
    }

    // ---------------- tiles ----------------

    private void BuildTiles()
    {
        _wifiTile = AddTile("", "Wi-Fi", split: true,
            toggle: async () => { if (_wifi != null) { await SystemControls.SetRadioAsync(_wifi, _wifi.State != RadioState.On); UpdateToggles(); } },
            more: () => SystemControls.OpenSettings("network-wifi"));
        _btTile = AddTile("", "Bluetooth", split: true,
            toggle: async () => { if (_bluetooth != null) { await SystemControls.SetRadioAsync(_bluetooth, _bluetooth.State != RadioState.On); UpdateToggles(); } },
            more: () => SystemControls.OpenSettings("bluetooth"));
        _airplaneTile = AddTile("", "Airplane mode", split: false,
            toggle: () =>
            {
                bool on = SystemControls.IsAirplaneModeOn() == true;
                if (!SystemControls.SetAirplaneMode(!on)) SystemControls.OpenSettings("network-airplanemode");
                Dispatcher.BeginInvoke(UpdateToggles, DispatcherPriority.Background);
                return Task.CompletedTask;
            });
        AddTile("", "Accessibility", split: false, chevron: true,
            toggle: () => { SystemControls.OpenSettings("easeofaccess"); Hide(); return Task.CompletedTask; });
        _saverTile = AddTile("", "Energy saver", split: false,
            // Windows has no supported way for apps to switch energy saver: open its setting.
            toggle: () => { SystemControls.OpenSettings("batterysaver"); Hide(); return Task.CompletedTask; });
        _captionsTile = AddTile("", "Live captions", split: false,
            toggle: async () =>
            {
                SystemControls.ToggleLiveCaptions();
                await Task.Delay(800);
                UpdateToggles();
            });
    }

    private Tile AddTile(string glyph, string label, bool split, Func<Task> toggle, Action? more = null, bool chevron = false)
    {
        // Toggles with their own settings page (Wi-Fi, Bluetooth) are pills; the rest are round buttons.
        var tile = new Tile(glyph, label, pill: split, toggle, more ?? (() => toggle()));
        (split ? (Panel)ToggleColumn : Tiles).Children.Add(tile.Root);
        return tile;
    }

    /// <summary>
    /// A Quick Settings toggle as its own piece of glass: a pill (round on/off button, name, state and an arrow to
    /// its settings) or a round button (name as tooltip). Filled with the accent colour while on.
    /// </summary>
    private sealed class Tile
    {
        private readonly Border _knob;
        private readonly TextBlock _glyph;
        private readonly TextBlock? _sub;
        private readonly string _name;
        private readonly bool _pill;
        private bool _on, _enabled = true;

        public FrameworkElement Root { get; }

        public Tile(string glyph, string label, bool pill, Func<Task> toggle, Action more)
        {
            _name = label;
            _pill = pill;
            var iconFont = (FontFamily)Application.Current.Resources["IconFont"];
            _glyph = new TextBlock { Text = glyph, FontFamily = iconFont, FontSize = pill ? 15 : 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            if (pill)
            {
                _knob = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(10, 0, 0, 0), Child = _glyph, Cursor = Cursors.Hand };
                var name = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                _sub = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
                _sub.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
                var chevron = new TextBlock { Text = "\uE76C", FontFamily = iconFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                chevron.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var info = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
                DockPanel.SetDock(chevron, Dock.Right);
                chevron.Margin = new Thickness(6, 0, 14, 0);
                info.Children.Add(chevron);
                info.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { name, _sub } });
                info.MouseLeftButtonUp += (_, _) => more();
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(info, 1);
                grid.Children.Add(_knob);
                grid.Children.Add(info);
                var tile = new GlassTile { CornerRadius = 30, Height = 60, Margin = new Thickness(0, 0, 0, 10) };
                tile.Children.Add(grid);
                Root = tile;
            }
            else
            {
                _knob = new Border { CornerRadius = new CornerRadius(31), Child = _glyph, Cursor = Cursors.Hand };
                var tile = new GlassTile { CornerRadius = 31, Width = 62, Height = 62, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = label };
                tile.Children.Add(_knob);
                Root = tile;
            }
            _knob.MouseLeftButtonUp += async (_, _) => await toggle();
            _knob.MouseEnter += (_, _) => { if (!_on) _knob.SetResourceReference(Border.BackgroundProperty, "HoverFill"); };
            _knob.MouseLeave += (_, _) => Apply();
            Apply();
        }

        public bool IsOn { get => _on; set { if (_on != value) { _on = value; Apply(); } } }
        public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; Root.Opacity = value ? 1 : 0.5; } } }

        /// <summary>The state line of a pill (network or device name); the plain name means "nothing to add".</summary>
        public string Label
        {
            get => _sub?.Text ?? _name;
            set { if (_sub != null) _sub.Text = value == _name ? (_on ? "On" : "Off") : value; }
        }

        private void Apply()
        {
            if (_on)
            {
                _knob.SetResourceReference(Border.BackgroundProperty, "QsAccent");
                _glyph.Foreground = Brushes.White;
            }
            else
            {
                if (_pill) _knob.SetResourceReference(Border.BackgroundProperty, "HoverFill");
                else _knob.Background = Brushes.Transparent;
                _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            }
            if (_sub != null && (_sub.Text is "On" or "Off" or "")) _sub.Text = _on ? "On" : "Off";
        }
    }

    // ---------------- handlers ----------------

    private void Media_PlayPause(object sender, MouseButtonEventArgs e) => MediaController.Instance.PlayPause();
    private void Media_Next(object sender, MouseButtonEventArgs e) => MediaController.Instance.Next();
    private void Media_Previous(object sender, MouseButtonEventArgs e) => MediaController.Instance.Previous();
    private void Media_ShowPlayer(object sender, MouseButtonEventArgs e) { MediaController.Instance.ShowPlayer(); Hide(); }

    private void Volume_ToggleMute(object sender, MouseButtonEventArgs e) { Audio.ToggleMute(); UpdateVolumeGlyph(); }
    private void Volume_Output(object sender, MouseButtonEventArgs e) { SystemControls.OpenSettings("sound"); Hide(); }
    private void OpenAllSettings(object sender, MouseButtonEventArgs e) { SystemControls.OpenSettings(""); Hide(); }
    private void OpenBatterySettings(object sender, MouseButtonEventArgs e) { SystemControls.OpenSettings("batterysaver"); Hide(); }

    /// <summary>Windows' accent colour (used by the tiles and sliders, like Windows' own Quick Settings).</summary>
    private static Color AccentColor()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch { return Color.FromRgb(0x3B, 0x82, 0xF6); }
    }
}
