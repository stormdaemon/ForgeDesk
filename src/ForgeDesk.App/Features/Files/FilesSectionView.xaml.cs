using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.Presentation.Files;

namespace ForgeDesk.App.Features.Files;

/// <summary>
/// The Files tab. View-only behavior: tree keyboard (Enter opens, Right expands, Left collapses or
/// goes to the parent), the "Go to file" box keys (Enter opens the best match, Down moves into the
/// list, Esc clears), scrolling a revealed row into view, and focusing the "Go to file" box.
/// </summary>
public partial class FilesSectionView : UserControl
{
    private FilesSectionViewModel? _viewModel;

    public FilesSectionView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as FilesSectionViewModel);
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded)
            {
                Attach(e.NewValue as FilesSectionViewModel);
            }
        };
    }

    private void Attach(FilesSectionViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        Detach();
        if (viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.RevealRequested += OnRevealRequested;
        viewModel.FocusGoToRequested += OnFocusGoToRequested;
    }

    private void Detach()
    {
        if (_viewModel is { } viewModel)
        {
            viewModel.RevealRequested -= OnRevealRequested;
            viewModel.FocusGoToRequested -= OnFocusGoToRequested;
            _viewModel = null;
        }
    }

    private void OnRevealRequested(object? sender, FileTreeNodeViewModel node) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!Tree.IsVisible)
            {
                return;
            }

            Tree.ScrollIntoView(node);
            if (Tree.ItemContainerGenerator.ContainerFromItem(node) is ListBoxItem item)
            {
                item.Focus();
            }
        }, DispatcherPriority.Loaded);

    private void OnFocusGoToRequested(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            GoToBox.Focus();
            GoToBox.SelectAll();
        }, DispatcherPriority.Input);

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var node = Tree.SelectedItem as FileTreeNodeViewModel;
        switch (e.Key)
        {
            case Key.Enter when node is not null:
                Execute(viewModel.ActivateNodeCommand, node);
                e.Handled = true;
                break;
            case Key.Right when node is { HasChevron: true }:
                Execute(viewModel.ExpandNodeCommand, node);
                e.Handled = true;
                break;
            case Key.Left when node is { IsFlat: false }:
                Execute(viewModel.CollapseNodeCommand, node);
                e.Handled = true;
                break;
            case Key.Escape when viewModel.IsGoToActive:
                viewModel.ClearGoToCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Execute(viewModel.AcceptGoToCommand, null);
                e.Handled = true;
                break;
            case Key.Escape when viewModel.IsGoToActive:
                viewModel.ClearGoToCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when Tree.Items.Count > 0:
                if (Tree.SelectedIndex < 0)
                {
                    Tree.SelectedIndex = 0;
                }

                Tree.ScrollIntoView(Tree.SelectedItem);
                if (Tree.ItemContainerGenerator.ContainerFromIndex(Tree.SelectedIndex) is ListBoxItem item)
                {
                    item.Focus();
                }

                e.Handled = true;
                break;
        }
    }

    private static void Execute(ICommand command, object? parameter)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
