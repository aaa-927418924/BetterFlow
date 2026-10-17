using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;

namespace BetterFlow.Services;

/// <summary>Optional stationary lens rim, separate from the working blur underneath.</summary>
public sealed class IndicatorPrismEffect : ShaderEffect
{
    private static readonly PixelShader PrismShader = new()
    {
        UriSource = new Uri("pack://application:,,,/BetterFlow;component/Assets/IndicatorPrism.ps", UriKind.Absolute)
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(IndicatorPrismEffect), 0);
    public static readonly DependencyProperty SurfaceSizeProperty = DependencyProperty.Register(
        nameof(SurfaceSize), typeof(System.Windows.Point), typeof(IndicatorPrismEffect),
        new UIPropertyMetadata(new System.Windows.Point(140, 38), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register(
        nameof(Refraction), typeof(double), typeof(IndicatorPrismEffect),
        new UIPropertyMetadata(5.5d, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty DispersionProperty = DependencyProperty.Register(
        nameof(Dispersion), typeof(double), typeof(IndicatorPrismEffect),
        new UIPropertyMetadata(0.9d, PixelShaderConstantCallback(2)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public System.Windows.Point SurfaceSize
    {
        get => (System.Windows.Point)GetValue(SurfaceSizeProperty);
        set => SetValue(SurfaceSizeProperty, value);
    }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double Dispersion { get => (double)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }

    public IndicatorPrismEffect()
    {
        PixelShader = PrismShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SurfaceSizeProperty);
        UpdateShaderValue(RefractionProperty);
        UpdateShaderValue(DispersionProperty);
    }
}
