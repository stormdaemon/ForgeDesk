using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.Presentation.Tasks;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>"Link a commit". Focuses the filter; Down moves to the list, double-click picks a commit.</summary>
public partial class PickCommitDialogView : UserControl
{
    public PickCommitDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        });
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && CommitList.Items.Count > 0)
        {
            CommitList.Focus();
            if (CommitList.ItemContainerGenerator.ContainerFromItem(CommitList.SelectedItem ?? CommitList.Items[0]) is ListBoxItem item)
            {
                item.Focus();
            }

            e.Handled = true;
        }
    }

    private void OnCommitDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PickCommitDialogViewModel dialog && e.OriginalSource is DependencyObject && CommitList.SelectedItem is CommitChoice choice)
        {
            dialog.ChooseCommand.Execute(choice);
            e.Handled = true;
        }
    }
}
