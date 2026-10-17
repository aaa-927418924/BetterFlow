using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;

namespace BetterFlow.Services;

public sealed class IndicatorGlassEffect : ShaderEffect
{
    private static readonly PixelShader GlassShader = new()
    {
        UriSource = new Uri("pack://application:,,,/BetterFlow;component/Assets/IndicatorGlass.ps", UriKind.Absolute)
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(IndicatorGlassEffect), 0);
    public static readonly DependencyProperty SurfaceSizeProperty = DependencyProperty.Register(
        nameof(SurfaceSize), typeof(System.Windows.Point), typeof(IndicatorGlassEffect),
        new UIPropertyMetadata(new System.Windows.Point(140, 38), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register(
        nameof(Refraction), typeof(double), typeof(IndicatorGlassEffect),
        new UIPropertyMetadata(4.5d, PixelShaderConstantCallback(1)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public System.Windows.Point SurfaceSize
    {
        get => (System.Windows.Point)GetValue(SurfaceSizeProperty);
        set => SetValue(SurfaceSizeProperty, value);
    }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }

    public IndicatorGlassEffect()
    {
        PixelShader = GlassShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SurfaceSizeProperty);
        UpdateShaderValue(RefractionProperty);
    }
}
