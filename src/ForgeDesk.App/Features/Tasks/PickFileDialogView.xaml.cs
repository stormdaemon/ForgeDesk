using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.Presentation.Tasks;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>"Link a file". Focuses the search; Down moves to the results, double-click picks a file.</summary>
public partial class PickFileDialogView : UserControl
{
    public PickFileDialogView()
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
        if (e.Key == Key.Down && FileList.Items.Count > 0)
        {
            FileList.Focus();
            if (FileList.ItemContainerGenerator.ContainerFromItem(FileList.SelectedItem ?? FileList.Items[0]) is ListBoxItem item)
            {
                item.Focus();
            }

            e.Handled = true;
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PickFileDialogViewModel dialog && FileList.SelectedItem is FileChoice choice)
        {
            dialog.ChooseCommand.Execute(choice);
            e.Handled = true;
        }
    }
}
