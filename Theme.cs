using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GlassDock;

/// <summary>Light/dark palette that follows the Windows "system" (taskbar) colour mode.</summary>
internal static class Theme
{
    public static bool IsLight { get; private set; }

    public static void Refresh()
    {
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { }
        IsLight = light;
        Apply();
    }

    private static void Apply()
    {
        var r = Application.Current.Resources;
        bool l = IsLight;
        r["Fg"] = Brush(l ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White);
        r["FgDim"] = Brush(l ? Color.FromArgb(0xB0, 0x14, 0x14, 0x14) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        r["Indicator"] = Brush(l ? Color.FromArgb(0xD0, 0x10, 0x10, 0x10) : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
        r["HoverFill"] = Brush(l ? Color.FromArgb(0x26, 0x00, 0x00, 0x00) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        r["MenuBg"] = Brush(l ? Color.FromArgb(0xF0, 0xF7, 0xF7, 0xF7) : Color.FromArgb(0xF0, 0x26, 0x26, 0x2A));
        // the same, solid: under each piece of a see-through panel when there's no refraction (GlassTile)
        r["PanelFill"] = Brush(l ? Color.FromRgb(0xF7, 0xF7, 0xF7) : Color.FromRgb(0x26, 0x26, 0x2A));
        r["MenuStroke"] = Brush(l ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        r["Divider"] = Brush(l ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        // Glass layers over the live blur: a tint, a faint body fill, a specular sheen and a bright rim.
        byte tint = (byte)Math.Clamp(Settings.Current.GlassOpacity * 255, 1, 255);
        r["GlassTint"] = Brush(l ? Color.FromArgb(tint, 0xF2, 0xF2, 0xF2) : Color.FromArgb(tint, 0x1C, 0x1C, 0x20));
        r["GlassFill"] = Light(
            (l ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0),
            (l ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x04, 0xFF, 0xFF, 0xFF), 1));

        // Thickness of the glass: light gathers along the edges, where they face the light (top left) and again on
        // the opposite side (bottom right), as on a rounded glass rim. (blurred into a smooth inner glow by GlassSurface)
        r["GlassEdge"] = Light(diagonal: true,
            (Color.FromArgb(l ? (byte)0xB0 : (byte)0x88, 0xFF, 0xFF, 0xFF), 0),
            (Color.FromArgb(l ? (byte)0x28 : (byte)0x14, 0xFF, 0xFF, 0xFF), 0.4),
            (Color.FromArgb(l ? (byte)0x28 : (byte)0x14, 0xFF, 0xFF, 0xFF), 0.6),
            (Color.FromArgb(l ? (byte)0x90 : (byte)0x60, 0xFF, 0xFF, 0xFF), 1));

        // Thin specular outline: a crisp highlight where the rim faces the light, fading along the sides, catching
        // the light again on the far corner.
        r["GlassRim"] = Light(diagonal: true,
            (Color.FromArgb(l ? (byte)0xE0 : (byte)0xC0, 0xFF, 0xFF, 0xFF), 0),
            (Color.FromArgb(l ? (byte)0x30 : (byte)0x1A, 0xFF, 0xFF, 0xFF), 0.38),
            (Color.FromArgb(l ? (byte)0x30 : (byte)0x1A, 0xFF, 0xFF, 0xFF), 0.62),
            (Color.FromArgb(l ? (byte)0xB0 : (byte)0x8C, 0xFF, 0xFF, 0xFF), 1));
        r["GlassSheen"] = Light(
            (Color.FromArgb(l ? (byte)0x48 : (byte)0x26, 0xFF, 0xFF, 0xFF), 0),
            (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.4));

        // A soft diagonal shine across the top-left, like light glancing off curved glass.
        var shine = new RadialGradientBrush
        {
            Center = new Point(0.18, -0.1), GradientOrigin = new Point(0.18, -0.1),
            RadiusX = 0.55, RadiusY = 1.1,
        };
        shine.GradientStops.Add(new GradientStop(Color.FromArgb(l ? (byte)0x50 : (byte)0x30, 0xFF, 0xFF, 0xFF), 0));
        shine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        shine.Opacity = Highlights;
        shine.Freeze();
        r["GlassShine"] = shine;
    }

    private static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Strength of the glass's white light layers (fill, edge glow, rim, sheen, shine), from settings.</summary>
    private static double Highlights => Math.Clamp(Settings.Current.GlassHighlights, 0, 1);

    /// <summary>A vertical gradient for one of the glass's white light layers, scaled by <see cref="Highlights"/>.</summary>
    private static LinearGradientBrush Light(params (Color c, double o)[] stops) => Light(false, stops);

    /// <summary>
    /// A gradient for one of the glass's white light layers, scaled by <see cref="Highlights"/>: top to bottom, or
    /// <paramref name="diagonal"/>ly from the top-left corner to the bottom-right one (light from the top left).
    /// </summary>
    private static LinearGradientBrush Light(bool diagonal, params (Color c, double o)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = diagonal ? new Point(1, 1) : new Point(0, 1), Opacity = Highlights };
        foreach (var (c, o) in stops) b.GradientStops.Add(new GradientStop(c, o));
        b.Freeze();
        return b;
    }
}
