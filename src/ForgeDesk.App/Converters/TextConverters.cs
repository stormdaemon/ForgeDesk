using System.Globalization;
using System.Windows.Data;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Converters;

/// <summary>
/// "3 min ago" from a DateTimeOffset or DateTime (null → "never"). As a multi-binding converter
/// the first value is the timestamp and any further value (typically <see cref="UiClock.Now"/>)
/// only forces a refresh, so labels stay current without view-model timers.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Format.RelativeTime(ToTimestamp(value));

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Format.RelativeTime(values is { Length: > 0 } ? ToTimestamp(values[0]) : null);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        [];

    private static DateTimeOffset? ToTimestamp(object? value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime { Kind: DateTimeKind.Unspecified } dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local)),
        DateTime dateTime => new DateTimeOffset(dateTime),
        _ => null,
    };
}

/// <summary>"2m 05s" from a TimeSpan (null → "—"); numbers are read as seconds.</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TimeSpan span => Format.Duration(span),
        double seconds when double.IsFinite(seconds) => Format.Duration(TimeSpan.FromSeconds(seconds)),
        int seconds => Format.Duration(TimeSpan.FromSeconds(seconds)),
        long seconds => Format.Duration(TimeSpan.FromSeconds(seconds)),
        _ => Format.Duration(null),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>"12.4 MB" from a byte count (any integer type); null → empty.</summary>
public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long bytes => Format.Bytes(bytes),
        int bytes => Format.Bytes(bytes),
        ulong bytes => Format.Bytes((long)Math.Min(bytes, long.MaxValue)),
        uint bytes => Format.Bytes(bytes),
        double bytes when double.IsFinite(bytes) && bytes >= 0 => Format.Bytes((long)Math.Min(bytes, long.MaxValue)),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Upper-cases text with the UI culture (section labels, keycaps); null → empty.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(culture) ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
