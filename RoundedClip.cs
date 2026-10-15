using System.Windows;
using System.Windows.Media;

namespace GlassDock;

/// <summary>
/// Clips an element, and everything in it, to a rounded rectangle of its own size. A Border's CornerRadius only
/// rounds its own background and border, and ClipToBounds clips to the square bounds, so a picture inside it (album
/// art) would keep square corners.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Apply(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        double r = GetRadius(element);
        if (r <= 0) { element.Clip = null; return; }
        var clip = new RectangleGeometry(new Rect(element.RenderSize), r, r);
        clip.Freeze();
        element.Clip = clip;
    }
}
