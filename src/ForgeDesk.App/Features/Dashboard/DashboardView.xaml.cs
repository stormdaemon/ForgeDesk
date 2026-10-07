using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ForgeDesk.Presentation.Dashboard;

namespace ForgeDesk.App.Features.Dashboard;

/// <summary>
/// The dashboard. View-only plumbing lives here: group headers follow the view model's IsGrouped,
/// folders dragged from Explorer are handed to AddFoldersCommand, and Ctrl+F focuses the search box.
/// </summary>
public partial class DashboardView : UserControl
{
    private DashboardViewModel? _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as DashboardViewModel);
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && SearchBox.IsVisible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue as DashboardViewModel);

    private void Attach(DashboardViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            ApplyGrouping();
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyGrouping();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsGrouped))
        {
            ApplyGrouping();
        }
    }

    /// <summary>Both lists share the collection's default view: group it by header when the view model asks.</summary>
    private void ApplyGrouping()
    {
        if (_viewModel is null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(_viewModel.Projects);
        if (view is null)
        {
            return;
        }

        var grouped = view.GroupDescriptions.Count > 0;
        if (_viewModel.IsGrouped == grouped)
        {
            return;
        }

        view.GroupDescriptions.Clear();
        if (_viewModel.IsGrouped)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectCardViewModel.GroupHeader)));
        }
    }

    // ----- Drag and drop from Explorer -------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var folders = DroppedFolders(e.Data);
        e.Effects = folders.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (_viewModel is null || !e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        // The view model reports the files it skips; folders are added.
        if (_viewModel.AddFoldersCommand.CanExecute(paths))
        {
            _viewModel.AddFoldersCommand.Execute(paths);
        }
    }

    private static IReadOnlyList<string> DroppedFolders(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(Directory.Exists).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}
