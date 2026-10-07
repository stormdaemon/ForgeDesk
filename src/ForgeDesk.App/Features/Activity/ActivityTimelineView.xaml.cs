using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Activity;

namespace ForgeDesk.App.Features.Activity;

/// <summary>
/// The activity timeline. Code-behind is view-only: it asks for the next page when the list is
/// scrolled near its end, opens an entry on double-click and focuses the search box on Ctrl+F.
/// </summary>
public partial class ActivityTimelineView : UserControl
{
    /// <summary>Distance from the end of the list (px) at which the next page is requested.</summary>
    private const double LoadMoreThreshold = 240;

    public ActivityTimelineView()
    {
        InitializeComponent();
        EntryList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private ActivityTimelineViewModel? ViewModel => DataContext as ActivityTimelineViewModel;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroller || ViewModel is not { HasMore: true } viewModel)
        {
            return;
        }

        // Also when the first page does not fill the viewport (nothing to scroll yet).
        if (scroller.VerticalOffset + scroller.ViewportHeight >= scroller.ExtentHeight - LoadMoreThreshold
            && viewModel.LoadMoreCommand.CanExecute(null))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ViewModel is not { } viewModel)
        {
            return;
        }

        var container = ItemsControl.ContainerFromElement(EntryList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is ActivityItemViewModel item && viewModel.OpenEntryCommand.CanExecute(item))
        {
            viewModel.OpenEntryCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
