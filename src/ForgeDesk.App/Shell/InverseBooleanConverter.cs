using System.Globalization;
using System.Windows.Data;

namespace ForgeDesk.App.Shell;

/// <summary><c>true</c> → <c>false</c> and back (e.g. a toggle that is not hit-testable while its popup is open).</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
