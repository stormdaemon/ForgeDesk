using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// The Changes view. View-only behavior: mirrors the file list selection to and from the view
/// model (which keeps it across status refreshes), keyboard shortcuts that invoke its commands
/// (Space stages, Enter goes to the diff, Delete discards, Ctrl+Enter commits, Ctrl+Shift+Enter
/// commits and pushes), the overflow menu, and focusing the summary box on request.
/// </summary>
public partial class ChangesView : UserControl
{
    private ChangesViewModel? _viewModel;
    private bool _syncingSelection;

    public ChangesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as ChangesViewModel);
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded)
            {
                Detach();
                Attach(e.NewValue as ChangesViewModel);
            }
        };
    }

    private void Attach(ChangesViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        Detach();
        _viewModel = viewModel;
        viewModel.SelectionRestored += OnSelectionRestored;
        viewModel.FocusSummaryRequested += OnFocusSummaryRequested;
        SyncSelectionFromViewModel();
        if (viewModel.IsSummaryFocusPending)
        {
            FocusSummary();
        }
    }

    private void Detach()
    {
        if (_viewModel is { } viewModel)
        {
            viewModel.SelectionRestored -= OnSelectionRestored;
            viewModel.FocusSummaryRequested -= OnFocusSummaryRequested;
            _viewModel = null;
        }
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && _viewModel is { } viewModel)
        {
            viewModel.SetSelection(FileList.SelectedItems.OfType<ChangeItemViewModel>().ToList());
        }
    }

    private void OnSelectionRestored(object? sender, EventArgs e) => SyncSelectionFromViewModel();

    /// <summary>Selects in the list what the view model selected; scrolls only when that changed the selection.</summary>
    private void SyncSelectionFromViewModel()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var wanted = viewModel.SelectedItems;
        var current = FileList.SelectedItems.OfType<ChangeItemViewModel>().ToHashSet();
        if (current.SetEquals(wanted) && current.Count == FileList.SelectedItems.Count)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            FileList.SelectedItems.Clear();
            foreach (var item in wanted)
            {
                FileList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        if (wanted.Count > 0)
        {
            FileList.ScrollIntoView(wanted[0]);
            if (FileList.IsKeyboardFocusWithin && FileList.ItemContainerGenerator.ContainerFromItem(wanted[0]) is ListBoxItem container)
            {
                container.Focus();
            }
        }
    }

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                Run(viewModel.ToggleStagingCommand, null);
                e.Handled = true;
                break;
            case Key.Enter:
                // The diff of the selection is already shown: Enter moves there, to scroll it with the keyboard.
                if (viewModel.ShowDiffView)
                {
                    DiffPane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }

                e.Handled = true;
                break;
            case Key.Delete:
                Run(viewModel.DiscardCommand, null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Ctrl+Enter commits and Ctrl+Shift+Enter commits and pushes, from anywhere in the view.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is not { } viewModel)
        {
            return;
        }

        switch (Keyboard.Modifiers)
        {
            case ModifierKeys.Control:
                Run(viewModel.CommitCommand, null);
                e.Handled = true;
                break;
            case ModifierKeys.Control | ModifierKeys.Shift:
                Run(viewModel.CommitAndPushCommand, null);
                e.Handled = true;
                break;
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

    private void OnFocusSummaryRequested(object? sender, EventArgs e) => FocusSummary();

    private void FocusSummary() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsVisible)
            {
                return;
            }

            SummaryBox.Focus();
            Keyboard.Focus(SummaryBox);
            SummaryBox.CaretIndex = SummaryBox.Text.Length;
            _viewModel?.AcknowledgeSummaryFocus();
        });

    private static void Run(ICommand command, object? parameter)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
