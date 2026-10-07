using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ForgeDesk.App.Features.Overview;

/// <summary>
/// Lays children out in one row, each as wide as its share of the total <see cref="WeightProperty"/>
/// (stacked bars such as the language bar). <see cref="Spacing"/> separates the segments.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static double GetWeight(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(WeightProperty);
    }

    public static void SetWeight(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        var widths = Widths(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Widths(finalSize.Width);
        var x = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (widths[i] <= 0)
            {
                child.Arrange(new Rect(x, 0, 0, finalSize.Height));
                continue;
            }

            child.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + Spacing;
        }

        return finalSize;
    }

    private double[] Widths(double total)
    {
        var count = InternalChildren.Count;
        var weights = new double[count];
        var sum = 0.0;
        var visible = 0;
        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            var weight = child.Visibility == Visibility.Collapsed ? 0 : Math.Max(0, GetWeight(child));
            if (double.IsNaN(weight) || double.IsInfinity(weight))
            {
                weight = 0;
            }

            weights[i] = weight;
            sum += weight;
            if (weight > 0)
            {
                visible++;
            }
        }

        var available = Math.Max(0, total - Spacing * Math.Max(0, visible - 1));
        var widths = new double[count];
        if (sum <= 0)
        {
            return widths;
        }

        for (var i = 0; i < count; i++)
        {
            widths[i] = weights[i] <= 0 ? 0 : Math.Max(2, available * weights[i] / sum);
        }

        return widths;
    }
}

/// <summary>A number (share, ratio) to a star <see cref="GridLength"/>, for bars made of two grid columns or rows.</summary>
public sealed class StarLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0.0,
        };

        return new GridLength(double.IsFinite(number) && number > 0 ? number : 0, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The arc of a circular gauge for a 0..1 value, starting at 12 o'clock, clockwise. The parameter is
/// "size,thickness" in pixels (the arc is centered in a size × size box).
/// </summary>
public sealed class ArcGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value switch
        {
            double d => d,
            int i => i / 100.0,
            _ => 0.0,
        };
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        var (size, thickness) = Parse(parameter as string);
        return Arc(fraction, size, thickness);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    public static Geometry Arc(double fraction, double size, double thickness)
    {
        if (fraction <= 0)
        {
            return Geometry.Empty;
        }

        var radius = (size - thickness) / 2;
        var center = new Point(size / 2, size / 2);
        if (fraction >= 0.9999)
        {
            return new EllipseGeometry(center, radius, radius);
        }

        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static (double Size, double Thickness) Parse(string? parameter)
    {
        var parts = (parameter ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var size = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 64;
        var thickness = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 6;
        return (size, thickness);
    }
}

/// <summary>Negates a boolean (e.g. IsEnabled while not busy).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
