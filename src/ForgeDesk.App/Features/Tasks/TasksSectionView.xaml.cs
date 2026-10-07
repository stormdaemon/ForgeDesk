using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ForgeDesk.Presentation.Tasks;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>
/// The Tasks tab. View-only behavior: drag and drop of cards (the drop position becomes a
/// <see cref="TaskDropRequest"/> for the view model's move command, with an insertion line while
/// dragging and auto-scroll near the column edges), click / Enter to open a card, Delete to delete
/// it, the width of the board's columns, and the resizable details pane.
/// </summary>
public partial class TasksSectionView : UserControl
{
    private const double MinColumnWidth = 260;
    private const double DefaultDetailWidth = 420;
    private const double MinDetailWidth = 360;

    private static readonly DataFormat CardFormat = DataFormats.GetDataFormat(typeof(TaskCardViewModel).FullName);

    private TasksSectionViewModel? _viewModel;
    private Point _dragOrigin;
    private TaskCardViewModel? _pressedCard;
    private bool _dragging;
    private DropIndicatorAdorner? _indicator;
    private ListBox? _indicatorHost;
    private double _detailWidth = DefaultDetailWidth;

    public TasksSectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TasksSectionViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) =>
        {
            Attach(DataContext as TasksSectionViewModel);
            UpdateBoardWidth();
        };
    }

    private void Attach(TasksSectionViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateDetailPane();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TasksSectionViewModel.IsDetailOpen):
                UpdateDetailPane();
                break;
            case nameof(TasksSectionViewModel.ShowDone):
                UpdateBoardWidth();
                break;
        }
    }

    // ----- Layout ------------------------------------------------------------------------

    private void UpdateDetailPane()
    {
        var open = _viewModel?.IsDetailOpen == true;
        if (open)
        {
            DetailColumn.MinWidth = MinDetailWidth;
            DetailColumn.Width = new GridLength(_detailWidth);
            DetailSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (DetailColumn.ActualWidth >= MinDetailWidth)
            {
                _detailWidth = DetailColumn.ActualWidth;
            }

            DetailColumn.MinWidth = 0;
            DetailColumn.Width = new GridLength(0);
            DetailSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBoardSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBoardWidth();

    /// <summary>Columns share the visible width, and never get narrower than <see cref="MinColumnWidth"/> (the board scrolls instead).</summary>
    private void UpdateBoardWidth()
    {
        var visibleColumns = _viewModel?.Columns.Count(c => c.IsVisible) ?? 5;
        var available = BoardScroll.ViewportWidth > 0
            ? BoardScroll.ViewportWidth
            : Math.Max(0, BoardScroll.ActualWidth - BoardScroll.Padding.Left - BoardScroll.Padding.Right);
        BoardColumns.Width = Math.Max(available, visibleColumns * MinColumnWidth);
    }

    // ----- Click, keyboard ------------------------------------------------------------------

    /// <summary>Ctrl+F: the filter box.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Esc anywhere in the tab (not handled by a box or a popup) closes the details pane.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _viewModel is { IsDetailOpen: true } vm)
        {
            vm.CloseDetailCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCardsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _pressedCard = null;
        if (e.OriginalSource is not DependencyObject source || FindAncestor<ButtonBase>(source) is not null || FindAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        _pressedCard = FindAncestor<ListBoxItem>(source)?.DataContext as TaskCardViewModel;
        _dragOrigin = e.GetPosition(this);
    }

    private void OnCardsMouseUp(object sender, MouseButtonEventArgs e)
    {
        var card = _pressedCard;
        _pressedCard = null;
        if (!_dragging && card is not null && _viewModel is { } vm)
        {
            vm.OpenCommand.Execute(card);
        }
    }

    private void OnCardsKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm || Keyboard.Modifiers != ModifierKeys.None
            || (e.OriginalSource as DependencyObject is { } source ? FindAncestor<ListBoxItem>(source)?.DataContext : null) is not TaskCardViewModel card)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.OpenCommand.Execute(card);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.DeleteCardCommand.Execute(card);
                e.Handled = true;
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is { SelectedCard: { } card } vm && e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is not null)
        {
            vm.OpenCommand.Execute(card);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { SelectedCard: { } card } vm || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.OpenCommand.Execute(card);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.DeleteCardCommand.Execute(card);
                e.Handled = true;
                break;
        }
    }

    // ----- Drag and drop -------------------------------------------------------------------

    private void OnCardsMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is not { } card || e.LeftButton != MouseButtonState.Pressed || _dragging || sender is not ListBox source)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragging = true;
        var container = source.ItemContainerGenerator.ContainerFromItem(card) as UIElement;
        if (container is not null)
        {
            container.Opacity = 0.45;
        }

        try
        {
            var data = new DataObject(CardFormat.Name, card);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        }
        finally
        {
            if (container is not null)
            {
                container.Opacity = 1;
            }

            RemoveIndicator();
            _pressedCard = null;
        }
    }

    private void OnCardsDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || !e.Data.GetDataPresent(CardFormat.Name))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var (_, lineY) = DropPosition(list, e.GetPosition(list));
        ShowIndicator(list, lineY);
        AutoScroll(list, e.GetPosition(list));
        e.Handled = true;
    }

    private void OnCardsDragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(list, _indicatorHost))
        {
            var position = e.GetPosition(list);
            if (position.X < 0 || position.Y < 0 || position.X > list.ActualWidth || position.Y > list.ActualHeight)
            {
                RemoveIndicator();
            }
        }
    }

    private void OnCardsDrop(object sender, DragEventArgs e)
    {
        RemoveIndicator();
        if (sender is not ListBox { Tag: TaskColumnViewModel column } list || _viewModel is not { } vm
            || e.Data.GetData(CardFormat.Name) is not TaskCardViewModel card)
        {
            return;
        }

        var (index, _) = DropPosition(list, e.GetPosition(list));
        var request = new TaskDropRequest(card, column.Status, index);
        if (vm.MoveCardCommand.CanExecute(request))
        {
            vm.MoveCardCommand.Execute(request);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Insertion index among the column's cards under the pointer (0 = top, Count = bottom) and the
    /// y of the insertion line. Only realized containers matter: the pointer is over the visible ones.
    /// </summary>
    private static (int Index, double LineY) DropPosition(ListBox list, Point position)
    {
        var count = list.Items.Count;
        double lastBottom = 4;
        var lastRealized = -1;
        for (var i = 0; i < count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container || !container.IsVisible)
            {
                continue;
            }

            var top = container.TranslatePoint(new Point(0, 0), list).Y;
            var bottom = top + container.ActualHeight;
            if (position.Y < top + (container.ActualHeight / 2))
            {
                return (i, Math.Max(2, top - 3));
            }

            lastBottom = bottom;
            lastRealized = i;
        }

        return (lastRealized < 0 ? count : lastRealized + 1, lastBottom + 2);
    }

    private void ShowIndicator(ListBox list, double y)
    {
        if (!ReferenceEquals(_indicatorHost, list))
        {
            RemoveIndicator();
            if (AdornerLayer.GetAdornerLayer(list) is { } layer)
            {
                _indicator = new DropIndicatorAdorner(list);
                _indicatorHost = list;
                layer.Add(_indicator);
            }
        }

        _indicator?.Update(y);
    }

    private void RemoveIndicator()
    {
        if (_indicator is not null && _indicatorHost is not null && AdornerLayer.GetAdornerLayer(_indicatorHost) is { } layer)
        {
            layer.Remove(_indicator);
        }

        _indicator = null;
        _indicatorHost = null;
    }

    private static void AutoScroll(ListBox list, Point position)
    {
        if (FindDescendant<ScrollViewer>(list) is not { } scroll)
        {
            return;
        }

        const double edge = 32;
        if (position.Y < edge)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12);
        }
        else if (position.Y > list.ActualHeight - edge)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12);
        }
    }

    // ----- Tree helpers ---------------------------------------------------------------------

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

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
