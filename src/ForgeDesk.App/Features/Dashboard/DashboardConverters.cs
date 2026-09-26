using System.Globalization;
using System.Windows.Data;

namespace ForgeDesk.App.Features.Dashboard;

/// <summary>
/// Shortens a path in the middle, keeping its root and its last folders ("C:\Users\me\…\apps\forge"),
/// so the most telling part stays visible. ConverterParameter: maximum length (default 48).
/// </summary>
public sealed class MiddleEllipsisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var max = parameter switch
        {
            int number => number,
            string raw when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 48,
        };

        return Shorten(text, Math.Max(12, max));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    public static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var separator = text.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var parts = text.Split(separator);
        if (parts.Length >= 3)
        {
            // Keep the root ("C:" or "") and as many trailing segments as fit.
            var head = parts[0] + separator;
            var tail = parts[^1];
            for (var i = parts.Length - 2; i > 0; i--)
            {
                var candidate = parts[i] + separator + tail;
                if (head.Length + 2 + candidate.Length > max)
                {
                    break;
                }

                tail = candidate;
            }

            var shortened = head + "…" + separator + tail;
            if (shortened.Length <= max)
            {
                return shortened;
            }
        }

        var keep = max - 1;
        var left = keep / 3;
        return string.Concat(text.AsSpan(0, left), "…", text.AsSpan(text.Length - (keep - left)));
    }
}
