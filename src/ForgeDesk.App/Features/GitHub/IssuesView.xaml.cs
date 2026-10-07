using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.GitHub;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>
/// Issues. View-only behavior: Enter or double-click opens the selected issue on GitHub, Ctrl+Enter in
/// the reply box posts the comment.
/// </summary>
public partial class IssuesView : UserControl
{
    public IssuesView()
    {
        InitializeComponent();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && DataContext is IssuesViewModel { SelectedItem: { } selected } issues)
        {
            issues.OpenOnGitHubCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is IssuesViewModel issues
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is ListBoxItem { DataContext: IssueRowViewModel row })
        {
            issues.OpenOnGitHubCommand.Execute(row);
        }
    }

    private void OnCommentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && DataContext is IssuesViewModel issues)
        {
            e.Handled = true;
            if (issues.AddCommentCommand.CanExecute(null))
            {
                issues.AddCommentCommand.Execute(null);
            }
        }
    }
}
