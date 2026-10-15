using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock;

/// <summary>
/// One piece of glass in a panel (a round button, a pill, a card): its content on its own refracting glass with its
/// own corner rounding. A panel window is a backdrop plus several of these with gaps between them, so each control
/// floats on the desktop as a separate piece of glass; they all refract the backdrop's single screen capture.
/// </summary>
public class GlassTile : Grid
{
    public GlassTile()
    {
        // Without refraction there's no picture of the screen for the glass to bend: each piece gets the panels'
        // own background instead, so it reads as it did on the panel (whose window is see-through all the same).
        if (!Settings.Current.Refraction)
        {
            var fill = new Border();
            fill.SetResourceReference(Border.BackgroundProperty, "PanelFill");
            fill.SetResourceReference(Border.CornerRadiusProperty, "Radius");
            Children.Add(fill);
        }
        Glass = new GlassSurface();
        Children.Add(Glass);
        Resources["Radius"] = new System.Windows.CornerRadius(20);
    }

    internal GlassSurface Glass { get; }

    /// <summary>Corner rounding of this piece of glass (half its height for a pill or a circle).</summary>
    public double CornerRadius
    {
        get => Resources["Radius"] is System.Windows.CornerRadius r ? r.TopLeft : 0;
        set => Resources["Radius"] = new System.Windows.CornerRadius(value);
    }

    /// <summary>Scales the width of this glass's refracting edge (1 = as wide as its corner rounding).</summary>
    public double BandScale
    {
        get => Glass.BandScale;
        set => Glass.BandScale = value;
    }

    /// <summary>A fixed width (DIPs) for this glass's refracting edge, instead of its corner rounding; 0 = none.</summary>
    public double BandWidth
    {
        get => Glass.BandWidth;
        set => Glass.BandWidth = value;
    }

    /// <summary>Points every tile inside <paramref name="body"/> at the panel's backdrop (see <see cref="GlassSurface.ShareFrom"/>).</summary>
    internal static void ConnectAll(GlassSurface backdrop, FrameworkElement body)
    {
        foreach (var tile in Descendants(body).OfType<GlassTile>()) tile.Glass.ShareFrom(backdrop, body);
    }

    /// <summary>
    /// Makes a panel window see-through wherever nothing is drawn, so the gaps between its pieces of glass show the
    /// live screen (composed by DWM) instead of the backdrop's capture, which is only as fresh as the last sample.
    /// The backdrop then keeps sampling for the glass but isn't shown. Needs WPF's hardware rendering (its software
    /// path can't draw see-through pixels into a normal window); returns whether it worked.
    /// </summary>
    internal static bool MakeSeeThrough(Window window, System.IntPtr hwnd, GlassSurface backdrop)
    {
        if (RenderCapability.Tier >> 16 == 0 || System.Windows.Interop.HwndSource.FromHwnd(hwnd) is not { CompositionTarget: { } target }) return false;
        var all = new Interop.Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (Interop.Native.DwmExtendFrameIntoClientArea(hwnd, ref all) != 0) return false;
        target.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent; // still hit-testable: a click in a gap closes the panel
        backdrop.HidePicture();
        return true;
    }

    /// <summary>Whether <paramref name="element"/> is (inside) a piece of glass, rather than a gap between them.</summary>
    internal static bool IsOnTile(DependencyObject? element)
    {
        for (var d = element; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is GlassTile) return true;
        return false;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
