using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassDock;

/// <summary>WPF wrapper for Shaders/Blur.fx: one direction of a Gaussian blur that is strongest over the glass's flat middle.</summary>
public sealed class GlassBlurEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/GlassDock;component/Shaders/Blur.ps"),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(GlassBlurEffect), 0);

    /// <summary>Full blur radius in uv units along this pass (e.g. (r/width, 0) for the horizontal pass).</summary>
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction), typeof(Point), typeof(GlassBlurEffect), new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(GlassBlurEffect), new UIPropertyMetadata(8.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(double), typeof(GlassBlurEffect), new UIPropertyMetadata(2.4, PixelShaderConstantCallback(2)));

    /// <summary>Corner radius in glass heights; matches LensEffect.</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(GlassBlurEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(3)));

    public GlassBlurEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(DirectionProperty);
        UpdateShaderValue(AspectProperty);
        UpdateShaderValue(BandProperty);
        UpdateShaderValue(RadiusProperty);
    }

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point Direction { get => (Point)GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }
    public double Band { get => (double)GetValue(BandProperty); set => SetValue(BandProperty, value); }
}
