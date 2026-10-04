using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassDock;

/// <summary>WPF wrapper for Shaders/Lens.fx: bends the background in a band along the glass's rim.</summary>
public sealed class LensEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/GlassDock;component/Shaders/Lens.ps"),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LensEffect), 0);

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(LensEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(LensEffect), new UIPropertyMetadata(8.0, PixelShaderConstantCallback(1)));

    /// <summary>Width of the refracting band along the rim, in glass heights.</summary>
    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(double), typeof(LensEffect), new UIPropertyMetadata(2.4, PixelShaderConstantCallback(3)));

    /// <summary>Corner radius of the band's rounded rectangle, in glass heights (at least the band width).</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(LensEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(4)));

    public LensEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(StrengthProperty);
        UpdateShaderValue(AspectProperty);
        UpdateShaderValue(BandProperty);
        UpdateShaderValue(RadiusProperty);
    }

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }
    public double Band { get => (double)GetValue(BandProperty); set => SetValue(BandProperty, value); }
}
