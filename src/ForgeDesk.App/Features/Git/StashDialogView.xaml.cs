using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ForgeDesk.App.Features.Git;

/// <summary>"Stash changes": optional message and new files. Focuses the message when shown.</summary>
public partial class StashDialogView : UserControl
{
    public StashDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            MessageField.Focus();
            Keyboard.Focus(MessageField);
        });
    }
}
