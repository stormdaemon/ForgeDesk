using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>The Tags view. View-only behavior: Enter or double-click shows the tagged commit, Delete deletes the tag.</summary>
public partial class TagsView : UserControl
{
    public TagsView()
    {
        InitializeComponent();
    }

    private void OnTagListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TagsViewModel tags || tags.SelectedTag is not { } selected || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        ICommand? command = e.Key switch
        {
            Key.Enter => tags.ShowInHistoryCommand,
            Key.Delete => tags.DeleteCommand,
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

    private void OnTagDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TagsViewModel tags
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(TagList, source) is ListBoxItem { DataContext: TagRowViewModel row })
        {
            tags.ShowInHistoryCommand.Execute(row);
        }
    }
}
