using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeDesk.App.Features.Terminal;

/// <summary>Visible while shells are being discovered and no terminal is open yet. Values: IsDiscovering, HasSessions.</summary>
public sealed class DiscoveringVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: 2 } && values[0] is true && values[1] is not true ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
