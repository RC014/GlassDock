using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassDock;

/// <summary>WPF wrapper for Shaders/Lens.fx: magnifies the background towards the bar's centre line.</summary>
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

    /// <summary>1 / (fraction of the half-height over which the effect ramps up from the rim).</summary>
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(
        nameof(Edge), typeof(double), typeof(LensEffect), new UIPropertyMetadata(2.4, PixelShaderConstantCallback(3)));

    public LensEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(StrengthProperty);
        UpdateShaderValue(AspectProperty);
        UpdateShaderValue(EdgeProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }
    public double Edge { get => (double)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
}
