using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>The Stashes view. View-only behavior: Enter restores the selected stash, Delete drops it.</summary>
public partial class StashesView : UserControl
{
    public StashesView()
    {
        InitializeComponent();
    }

    private void OnStashListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not StashesViewModel stashes || stashes.SelectedStash is not { } selected || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Enter => stashes.PopCommand,
            Key.Delete => stashes.DropCommand,
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
}
