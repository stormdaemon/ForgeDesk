using System.Globalization;
using System.Windows.Data;

namespace ForgeDesk.App.Shell;

/// <summary><c>true</c> when the value is null (e.g. an operation without known progress is indeterminate).</summary>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
