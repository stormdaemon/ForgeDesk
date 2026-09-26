using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.App.Features.Workspace;

/// <summary>
/// Branch dropdown of the workspace header. View behavior only: focus the filter when the flyout
/// opens, Down moves into the list, Esc closes and returns focus to the button.
/// </summary>
public partial class BranchSelector : UserControl
{
    public BranchSelector()
    {
        InitializeComponent();
    }

    private void OnFlyoutOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            FilterBox.Focus();
            Keyboard.Focus(FilterBox);
        });

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                FilterBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                break;
            case Key.Escape when DataContext is BranchSelectorViewModel selector:
                selector.IsOpen = false;
                Toggle.Focus();
                e.Handled = true;
                break;
        }
    }
}
