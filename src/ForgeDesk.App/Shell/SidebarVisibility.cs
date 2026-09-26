using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeDesk.App.Shell;

/// <summary>
/// Visibility of the PINNED / RECENT section headers: text headers in the full sidebar, a thin
/// divider in the collapsed rail, nothing when the section is empty. Values: [hasItems, isCollapsed].
/// </summary>
public static class SidebarVisibility
{
    public static IMultiValueConverter HeaderConverter { get; } = new SectionConverter(showWhenCollapsed: false);

    public static IMultiValueConverter RailDividerConverter { get; } = new SectionConverter(showWhenCollapsed: true);

    private sealed class SectionConverter(bool showWhenCollapsed) : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            var hasItems = values is { Length: > 0 } && values[0] is true;
            var collapsed = values is { Length: > 1 } && values[1] is true;
            return hasItems && collapsed == showWhenCollapsed ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
    }
}
