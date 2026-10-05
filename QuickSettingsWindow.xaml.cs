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

    // click-outside-to-close (same approach as the hidden-icons flyout)
    private IntPtr _mouseHook;
    private Native.LowLevelMouseProc? _mouseProc;

    public QuickSettingsWindow()
    {
        InitializeComponent();
        _anim = new PanelAnimation(this);
        Resources["QsAccent"] = new SolidColorBrush(AccentColor());
        if (!Settings.Current.Refraction) Glass.Visibility = Visibility.Collapsed;
        BuildTiles();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int none = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(int));
            if (Settings.Current.Refraction) Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };

        MediaController.Instance.Changed += UpdateMedia;
        _poll.Tick += (_, _) => { UpdateVolume(); UpdateBattery(); UpdateToggles(); };
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
        int? brightness = SystemControls.GetBrightness();
        BrightnessRow.Visibility = brightness == null ? Visibility.Collapsed : Visibility.Visible;
        if (brightness != null) { _updating = true; BrightnessSlider.Value = brightness.Value; _updating = false; }

        _wifi ??= await SystemControls.GetRadioAsync(RadioKind.WiFi);
        _bluetooth ??= await SystemControls.GetRadioAsync(RadioKind.Bluetooth);
        UpdateToggles();
        _btTile.Label = await SystemControls.ConnectedBluetoothDeviceAsync() ?? "Bluetooth";
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
        MediaCard.Visibility = MediaDivider.Visibility = m.HasMedia ? Visibility.Visible : Visibility.Collapsed;
        if (!m.HasMedia) return;
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
        var tile = new Tile(glyph, label, split || chevron, toggle, more ?? (() => toggle()));
        Tiles.Children.Add(tile.Root);
        return tile;
    }

    /// <summary>A Quick Settings tile: a rounded button (optionally split, with a chevron) and a label under it.</summary>
    private sealed class Tile
    {
        private readonly Border _button;
        private readonly TextBlock _label;
        private readonly TextBlock[] _glyphs;
        private readonly Border? _splitLine;
        private bool _on, _enabled = true;

        public FrameworkElement Root { get; }

        public Tile(string glyph, string label, bool withChevron, Func<Task> toggle, Action more)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid();
            if (withChevron)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var main = new Border { Background = Brushes.Transparent, Child = icon, Cursor = Cursors.Hand };
                main.MouseLeftButtonUp += async (_, _) => await toggle();
                var chev = new TextBlock { Text = "", FontFamily = icon.FontFamily, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var side = new Border { Background = Brushes.Transparent, Child = chev, Cursor = Cursors.Hand };
                side.MouseLeftButtonUp += (_, _) => more();
                Grid.SetColumn(side, 1);
                _splitLine = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
                Grid.SetColumn(_splitLine, 1);
                grid.Children.Add(main);
                grid.Children.Add(side);
                grid.Children.Add(_splitLine);
                _glyphs = new[] { icon, chev };
            }
            else
            {
                grid.Children.Add(icon);
                grid.Background = Brushes.Transparent;
                grid.Cursor = Cursors.Hand;
                grid.MouseLeftButtonUp += async (_, _) => await toggle();
                _glyphs = new[] { icon };
            }

            _button = new Border { Height = 50, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid };
            _label = new TextBlock { Text = label };
            _label.SetResourceReference(FrameworkElement.StyleProperty, "QsLabel");
            Root = new StackPanel { Margin = new Thickness(4, 0, 4, 10), Children = { _button, _label } };
            Apply();
        }

        public bool IsOn { get => _on; set { if (_on != value) { _on = value; Apply(); } } }
        public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; Root.Opacity = value ? 1 : 0.5; } } }
        public string Label { get => _label.Text; set => _label.Text = value; }

        private void Apply()
        {
            if (_on)
            {
                _button.SetResourceReference(Border.BackgroundProperty, "QsAccent");
                _button.BorderBrush = Brushes.Transparent;
                foreach (var g in _glyphs) g.Foreground = Brushes.White;
                if (_splitLine != null) _splitLine.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                _button.SetResourceReference(Border.BackgroundProperty, "HoverFill");
                _button.SetResourceReference(Border.BorderBrushProperty, "MenuStroke");
                foreach (var g in _glyphs) g.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                _splitLine?.SetResourceReference(Border.BackgroundProperty, "Divider");
            }
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
