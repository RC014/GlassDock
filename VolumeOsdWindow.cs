using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// The glass volume / brightness indicator shown instead of Windows' pop-up: a capsule near the bottom
/// of the screen with a speaker or sun glyph, a level bar and the percentage. Fades out shortly after the last change.
/// </summary>
internal sealed class VolumeOsdWindow : Window
{
    private const double W = 250, H = 48;
    private readonly TextBlock _glyph, _percent;
    private readonly Border _fill;
    private readonly Grid _track;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Grid _root;

    public VolumeOsdWindow()
    {
        Title = "GlassDock Volume";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Width = W;
        Height = H;
        UseLayoutRounding = true;
        Resources["Radius"] = new CornerRadius(H / 2);

        _root = new Grid();
        if (Settings.Current.Refraction) _root.Children.Add(new GlassSurface());
        else _root.Children.Add(Rounded(new Border { BorderThickness = new Thickness(1) }, "MenuBg", "MenuStroke"));

        var row = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

        _glyph = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Width = 22 };
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        row.Children.Add(_glyph);

        _track = new Grid { Height = 4, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_track, 1);
        _track.Children.Add(Rounded(new Border(), "Divider", null));
        _fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(AccentColor()) };
        _track.Children.Add(_fill);
        row.Children.Add(_track);

        _percent = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        _percent.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _percent.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Grid.SetColumn(_percent, 2);
        row.Children.Add(_percent);

        _root.Children.Add(row);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            // click-through, never activated, not in Alt+Tab
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x20 /*WS_EX_TRANSPARENT*/) & ~Native.WS_EX_APPWINDOW);
            if (Settings.Current.Refraction) Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };
    }

    /// <summary>Shows (or refreshes) the indicator with the current volume.</summary>
    public void ShowLevel()
    {
        var v = Audio.Get();
        double level = v?.Level ?? 0;
        bool muted = v == null || v.Value.Muted;
        Show((char)(muted || level < 0.01 ? 0xE74F : level < 0.34 ? 0xE993 : level < 0.67 ? 0xE994 : 0xE995), level, muted);
    }

    /// <summary>Shows (or refreshes) the indicator with a screen brightness (0–100).</summary>
    public void ShowBrightness(int percent) => Show((char)0xE706, percent / 100.0, false);

    private void Show(char glyph, double level, bool dim)
    {
        _glyph.Text = glyph.ToString();
        _percent.Text = Math.Round(level * 100).ToString();
        _fill.Opacity = dim ? 0.4 : 1;

        if (!IsVisible)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Round(area.Left + (area.Width - W) / 2);
            Top = Math.Round(area.Bottom - H - 84); // clear of the dock
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 1;
            Show();
        }
        else
        {
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 1;
        }
        UpdateLayout();
        _fill.Width = Math.Max(0, _track.ActualWidth * level);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (!_hideTimer.IsEnabled && _root.Opacity < 0.01) Hide(); };
        _root.BeginAnimation(OpacityProperty, fade);
    }

    private static Border Rounded(Border b, string fill, string? stroke)
    {
        b.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        b.SetResourceReference(Border.BackgroundProperty, fill);
        if (stroke != null) b.SetResourceReference(Border.BorderBrushProperty, stroke);
        return b;
    }

    private static Color AccentColor()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch { return Color.FromRgb(0x76, 0xB9, 0xED); }
    }
}
