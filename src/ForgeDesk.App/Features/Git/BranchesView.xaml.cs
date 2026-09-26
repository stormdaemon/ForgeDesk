using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// The Branches view. View-only behavior: Enter or double-click switches to the selected branch,
/// F2 renames it, Delete deletes it, and the overflow menu.
/// </summary>
public partial class BranchesView : UserControl
{
    public BranchesView()
    {
        InitializeComponent();
    }

    private void OnBranchListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BranchesViewModel branches || branches.SelectedBranch is not { } selected || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Enter => branches.SwitchCommand,
            Key.F2 => branches.RenameCommand,
            Key.Delete => branches.DeleteCommand,
            _ => null,
        };

        if (command is not null)
        {
            if (command.CanExecute(selected))
            {
                command.Execute(selected);
            }

            e.Handled = true;
        }
    }

    private void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BranchesViewModel branches
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(BranchList, source) is ListBoxItem { DataContext: BranchRowViewModel row }
            && branches.SwitchCommand.CanExecute(row))
        {
            branches.SwitchCommand.Execute(row);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Resources["MoreMenu"] is ContextMenu menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
