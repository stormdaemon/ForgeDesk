using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ForgeDesk.App.Features.Git;

/// <summary>Asks for the name and email recorded in commits. Focuses the name field when shown.</summary>
public partial class GitIdentityDialogView : UserControl
{
    public GitIdentityDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
        });
    }
}
