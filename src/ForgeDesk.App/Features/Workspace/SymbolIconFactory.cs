using System.Globalization;
using System.Windows.Data;
using ForgeDesk.App.Converters;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Features.Workspace;

/// <summary>
/// Creates a <see cref="SymbolIcon"/> from a SymbolRegular name, for icon properties bound to view
/// models (<c>ui:Button.Icon</c>). A new element per binding: icon elements cannot be shared.
/// </summary>
public static class SymbolIconFactory
{
    public static IValueConverter Converter { get; } = new SymbolIconConverter();

    private sealed class SymbolIconConverter : IValueConverter
    {
        private static readonly SymbolFromNameConverter Names = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            new SymbolIcon { Symbol = (SymbolRegular)Names.Convert(value, typeof(SymbolRegular), parameter, culture) };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
