using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeDesk.App.Converters;

/// <summary><c>true</c> → Visible; <c>false</c> or null → Collapsed (Hidden with <see cref="UseHidden"/>).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Keep the layout slot of hidden elements.</summary>
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary><c>true</c> → Collapsed; <c>false</c> or null → Visible.</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>
/// Visible when the value is present: not null and, for text, not blank. Set
/// <see cref="Invert"/> to show an element only while the value is missing.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true,
        };

        return present ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Collapsed for zero (any numeric type), empty collections and null; Visible otherwise.
/// Used for badges and counters ("3 changes").
/// </summary>
public sealed class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value switch
        {
            null => true,
            int i => i == 0,
            long l => l == 0,
            double d => d == 0 || double.IsNaN(d),
            float f => f == 0 || float.IsNaN(f),
            decimal m => m == 0,
            short s => s == 0,
            uint u => u == 0,
            ulong ul => ul == 0,
            ICollection collection => collection.Count == 0,
            _ => false,
        };

        return isZero ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
