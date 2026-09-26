using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.Presentation.Palette;

namespace ForgeDesk.App.Features.Palette;

/// <summary>
/// The palette card shown in the shell's popup. View behavior only: focus the search field when
/// shown, keep the selection scrolled into view, run an entry on click.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        // The popup window must exist before its content can take keyboard focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.CaretIndex = QueryBox.Text.Length;
        });
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is { } selected)
        {
            Results.ScrollIntoView(selected);
        }
    }

    private void OnResultsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel palette)
        {
            return;
        }

        var container = FindContainer(e.OriginalSource as DependencyObject);
        if (container?.DataContext is PaletteResultViewModel result)
        {
            e.Handled = true;
            palette.ExecuteCommand.Execute(result);
        }
    }

    private static ListBoxItem? FindContainer(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }
}
