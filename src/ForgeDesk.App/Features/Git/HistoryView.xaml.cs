using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// The History view. View-only behavior: loads the next page when scrolling near the end, keeps
/// the selected commit visible (navigation, parent links), and Ctrl+F focuses the search box.
/// </summary>
public partial class HistoryView : UserControl
{
    /// <summary>Distance from the end of the list (in pixels) at which the next page is requested.</summary>
    private const double LoadMoreThreshold = 600;

    public HistoryView()
    {
        InitializeComponent();
    }

    private void OnCommitListScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not HistoryViewModel history || e.ExtentHeight <= 0)
        {
            return;
        }

        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - LoadMoreThreshold && history.LoadMoreCommand.CanExecute(null))
        {
            history.LoadMoreCommand.Execute(null);
        }
    }

    private void OnCommitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitList.SelectedItem is { } selected)
        {
            CommitList.ScrollIntoView(selected);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
