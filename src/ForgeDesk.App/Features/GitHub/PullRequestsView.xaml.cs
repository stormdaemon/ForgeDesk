using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.GitHub;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>Pull requests. View-only behavior: Enter or double-click opens the selected pull request on GitHub.</summary>
public partial class PullRequestsView : UserControl
{
    public PullRequestsView()
    {
        InitializeComponent();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && DataContext is PullRequestsViewModel { SelectedItem: { } selected } pulls)
        {
            pulls.OpenOnGitHubCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PullRequestsViewModel pulls
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is ListBoxItem { DataContext: PullRequestRowViewModel row })
        {
            pulls.OpenOnGitHubCommand.Execute(row);
        }
    }
}
