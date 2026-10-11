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
