using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ForgeDesk.App.Controls;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Converters;

/// <summary>
/// Brush for a status: accepts <see cref="StatusKind"/>, RunStatus, CiState, NotificationSeverity,
/// ActivityOutcome, AttentionLevel or a status name. ConverterParameter "Subtle" returns the
/// translucent background variant (for pills and row highlights).
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = StatusKinds.From(value);
        var subtle = parameter is string p && p.Equals("Subtle", StringComparison.OrdinalIgnoreCase);
        return BrushFor(kind, subtle);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    /// <summary>
    /// The theme brush for a status. The brushes are recolored in place on theme changes, so
    /// the returned instance stays correct.
    /// </summary>
    public static Brush BrushFor(StatusKind kind, bool subtle = false)
    {
        var name = kind switch
        {
            StatusKind.Success => "ForgeSuccess",
            StatusKind.Warning => "ForgeWarning",
            StatusKind.Danger => "ForgeDanger",
            StatusKind.Info => "ForgeInfo",
            StatusKind.Running => subtle ? "ForgeAccent" : "ForgeRunning",
            _ => "ForgeNeutral",
        };

        var key = subtle ? name + "SubtleBrush" : name + "Brush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

/// <summary>Converts any supported domain state to a <see cref="StatusKind"/> (for StatusDot/Pill bindings).</summary>
public sealed class ToStatusKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        StatusKinds.From(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Converts a SymbolRegular name ("Branch24") to the symbol. Unknown names fall back to the
/// ConverterParameter name, then to <see cref="SymbolRegular.Empty"/>.
/// </summary>
public sealed class SymbolFromNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SymbolRegular symbol)
        {
            return symbol;
        }

        if (TryParse(value as string, out symbol) || TryParse(parameter as string, out symbol))
        {
            return symbol;
        }

        return SymbolRegular.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SymbolRegular symbol ? symbol.ToString() : Binding.DoNothing;

    private static bool TryParse(string? name, out SymbolRegular symbol)
    {
        symbol = SymbolRegular.Empty;
        return !string.IsNullOrWhiteSpace(name)
            && Enum.TryParse(name.Trim(), ignoreCase: true, out symbol)
            && Enum.IsDefined(symbol);
    }
}
