using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ForgeDesk.Presentation.Commands;

namespace ForgeDesk.App.Features.Commands;

/// <summary>
/// The Commands tab. View-only behavior: keyboard shortcuts of the commands list (Enter runs or
/// stops, F2 edits, Delete removes a custom command), double-click to run, and Ctrl+F to filter.
/// </summary>
public partial class CommandsSectionView : UserControl
{
    public CommandsSectionView()
    {
        InitializeComponent();
    }

    private CommandsSectionViewModel? ViewModel => DataContext as CommandsSectionViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !IsInRunDetail(e.OriginalSource as DependencyObject))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnCommandListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedCommand is not { } row || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Enter => vm.RunOrStopCommand,
            Key.F2 when row.IsCustom => vm.EditCommandCommand,
            Key.Delete when row.IsCustom => vm.DeleteCommandCommand,
            _ => null,
        };

        if (command is not null)
        {
            if (command.CanExecute(row))
            {
                command.Execute(row);
            }

            e.Handled = true;
        }
    }

    private void OnCommandListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a command row (not on its buttons or the scroll bar) runs it.
        if (ViewModel is not { } vm || e.OriginalSource is not DependencyObject source || FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is CommandRowViewModel { IsRunning: false } row && vm.RunOrStopCommand.CanExecute(row))
        {
            vm.RunOrStopCommand.Execute(row);
            e.Handled = true;
        }
    }

    private static bool IsInRunDetail(DependencyObject? source) => source is not null && FindAncestor<RunDetailView>(source) is not null;

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }
}
