using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ForgeDesk.App.Controls;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Shell;

/// <summary>Maps a view model <see cref="StatusTone"/> to the <see cref="StatusKind"/> of StatusDot and Pill.</summary>
public sealed class StatusToneToKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToKind(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    public static StatusKind ToKind(object? value) => value switch
    {
        StatusTone.Success => StatusKind.Success,
        StatusTone.Warning => StatusKind.Warning,
        StatusTone.Danger => StatusKind.Danger,
        StatusTone.Info => StatusKind.Info,
        StatusTone.Running => StatusKind.Running,
        _ => StatusKind.Neutral,
    };
}

/// <summary>Collapsed for <see cref="StatusTone.None"/> (nothing to show), Visible otherwise.</summary>
public sealed class StatusToneToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StatusTone tone && tone != StatusTone.None ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Brush for a badge of the given tone; ConverterParameter "Subtle" returns the translucent variant.</summary>
public sealed class StatusToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var subtle = parameter is string p && p.Equals("Subtle", StringComparison.OrdinalIgnoreCase);
        return Converters.StatusToBrushConverter.BrushFor(StatusToneToKindConverter.ToKind(value), subtle);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
