using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GlassDock;

/// <summary>A small settings window; Save writes settings.json and reloads GlassDock to apply.</summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly Settings _s = Settings.Current;
    private readonly StackPanel _rows = new() { Margin = new Thickness(22, 16, 22, 8) };

    public static void ShowSingle()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        Title = "GlassDock Settings";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/GlassDock;component/assets/GlassDock.ico"));
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "MenuBg");
        SetResourceReference(ForegroundProperty, "Fg");

        Section("Dock");
        Slider("Icon size", () => _s.IconSize, v => _s.IconSize = v, 24, 72, 1, "0");
        Slider("Hover magnification", () => _s.Magnification, v => _s.Magnification = v, 1, 2, 0.05, "0.00×");
        Check("Show Start button", () => _s.ShowStartButton, v => _s.ShowStartButton = v);
        Check("Show media player next to the dock", () => _s.ShowMediaPlayer, v => _s.ShowMediaPlayer = v);
        Slider("Media player width", () => _s.MediaPlayerWidth, v => _s.MediaPlayerWidth = v, 200, 600, 10, "0");
        Check("Media player: show cover", () => _s.ShowMediaArt, v => _s.ShowMediaArt = v);
        Check("Media player: show title", () => _s.ShowMediaTitle, v => _s.ShowMediaTitle = v);
        Check("Media player: show artist", () => _s.ShowMediaArtist, v => _s.ShowMediaArtist = v);

        Section("Look");
        Slider("Corner roundness", () => _s.CornerRadius, v => _s.CornerRadius = v, 0, 30, 1, "0");
        Check("Real glass refraction (bars won't appear in screenshots)", () => _s.Refraction, v => _s.Refraction = v);
        Slider("Refraction strength", () => _s.RefractionStrength, v => _s.RefractionStrength = v, 0, 1, 0.05, "0%", percent: true);
        Slider("Edge softness (how gradually refraction and blur build up from the rim)", () => _s.RefractionEdge, v => _s.RefractionEdge = v, 0.1, 1, 0.05, "0%", percent: true);
        Slider("Blur (strongest at the centre)", () => _s.RefractionBlur, v => _s.RefractionBlur = v, 0, 1, 0.05, "0%", percent: true);
        Check("Frosted glass when refraction is off (Windows blur)", () => _s.GlassBlur >= 0.5, v => _s.GlassBlur = v ? 1 : 0);
        Slider("Glass highlights (white shine and edges)", () => _s.GlassHighlights, v => _s.GlassHighlights = v, 0, 1, 0.05, "0%", percent: true);
        Slider("Glass tint (lower = clearer)", () => _s.GlassOpacity, v => _s.GlassOpacity = v, 0, 0.8, 0.01, "0%", percent: true);
        Slider("Gap from screen edges", () => _s.BottomMargin, v => _s.BottomMargin = _s.SideMargin = v, 0, 24, 1, "0");

        Section("Behaviour");
        Check("Auto-hide (show when the cursor touches the bottom edge)", () => _s.AutoHide, v => _s.AutoHide = v);
        Check("Hide the Windows taskbar", () => _s.HideWindowsTaskbar, v => _s.HideWindowsTaskbar = v);
        Check("Start with Windows", () => App.StartsWithWindows, v => App.StartsWithWindows = v);

        Section("Clock");
        Check("Show seconds", () => _s.ShowSeconds, v => _s.ShowSeconds = v);
        Check("Show date", () => _s.ShowDate, v => _s.ShowDate = v);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 14) };
        buttons.Children.Add(Button("Open settings file", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Settings.FilePath}\"") { UseShellExecute = true })));
        buttons.Children.Add(Button("Cancel", Close));
        var save = Button("Save & apply", () => { _saved = true; Settings.Save(); Close(); App.Restart(); });
        save.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        save.Foreground = Brushes.White;
        buttons.Children.Add(save);
        _rows.Children.Add(buttons);

        Content = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        // Closing without saving throws away the edits (they were made on the live settings object).
        Closing += (_, _) => { if (!_saved) Settings.Load(); };
    }

    private bool _saved;

    private void Section(string title)
    {
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };
        _rows.Children.Add(t);
    }

    private void Slider(string label, Func<double> get, Action<double> set, double min, double max, double step, string format, bool percent = false)
    {
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        value.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        head.Children.Add(new TextBlock { Text = label });

        // Clicking the track jumps to that point; arrow keys / page keys move by steps sized to the range
        // (WPF's default LargeChange of 1 made 0..1 sliders jump straight from end to end).
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max),
            TickFrequency = step, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            SmallChange = step, LargeChange = Math.Max(step, (max - min) / 10),
        };
        void Show() => value.Text = percent ? (slider.Value * 100).ToString("0") + "%" : slider.Value.ToString(format);
        slider.ValueChanged += (_, _) => { set(slider.Value); Show(); };
        Show();

        _rows.Children.Add(head);
        _rows.Children.Add(slider);
    }

    private void Check(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 6, 0, 0) };
        cb.SetResourceReference(ForegroundProperty, "Fg");
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        _rows.Children.Add(cb);
    }

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }
}
