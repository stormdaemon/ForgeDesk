using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.GitHub;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>GitHub Actions. View-only behavior: Enter or double-click opens the selected run (and its logs) on GitHub.</summary>
public partial class ActionsView : UserControl
{
    public ActionsView()
    {
        InitializeComponent();
    }

    private void OnRunListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && DataContext is ActionsViewModel { SelectedRun: { } selected } actions)
        {
            actions.OpenOnGitHubCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void OnRunDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ActionsViewModel actions
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(RunList, source) is ListBoxItem { DataContext: WorkflowRunRowViewModel row })
        {
            actions.OpenOnGitHubCommand.Execute(row);
        }
    }
}
