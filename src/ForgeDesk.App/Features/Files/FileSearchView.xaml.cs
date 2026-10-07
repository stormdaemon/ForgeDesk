using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.Presentation.Files;

namespace ForgeDesk.App.Features.Files;

/// <summary>
/// "Search in files". View-only behavior: Enter runs the search, Esc cancels it or goes back to
/// the tree, selecting a result previews it (Enter too), and the query box takes the focus when
/// the view model asks (Ctrl+Shift+F, "Search in folder").
/// </summary>
public partial class FileSearchView : UserControl
{
    private FileSearchViewModel? _viewModel;

    public FileSearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as FileSearchViewModel);
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded)
            {
                Attach(e.NewValue as FileSearchViewModel);
            }
        };
    }

    private void Attach(FileSearchViewModel? viewModel)
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
        viewModel.FocusRequested += OnFocusRequested;
    }

    private void Detach()
    {
        if (_viewModel is { } viewModel)
        {
            viewModel.FocusRequested -= OnFocusRequested;
            _viewModel = null;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        }, DispatcherPriority.Input);

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Execute(viewModel.SearchCommand, null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (viewModel.IsSearching)
                {
                    Execute(viewModel.CancelCommand, null);
                }
                else if (FindSection() is { } section)
                {
                    Execute(section.CloseSearchCommand, null);
                }

                e.Handled = true;
                break;
            case Key.Down when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex);
                Results.ScrollIntoView(Results.SelectedItem);
                if (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) is ListBoxItem item)
                {
                    item.Focus();
                }

                e.Handled = true;
                break;
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is { } viewModel && Results.SelectedItem is SearchResultRow row)
        {
            Execute(viewModel.OpenResultCommand, row);
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter && Results.SelectedItem is SearchResultRow row)
        {
            Execute(viewModel.OpenResultCommand, row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindSection() is { } section)
        {
            Execute(section.CloseSearchCommand, null);
            e.Handled = true;
        }
    }

    private FilesSectionViewModel? FindSection()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FilesSectionView view)
            {
                return view.DataContext as FilesSectionViewModel;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void Execute(ICommand command, object? parameter)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
