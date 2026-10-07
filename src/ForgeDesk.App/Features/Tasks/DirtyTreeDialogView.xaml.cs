using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>"You have uncommitted changes": stash and switch, create without switching, or cancel.</summary>
public partial class DirtyTreeDialogView : UserControl
{
    public DirtyTreeDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            StashButton.Focus();
            Keyboard.Focus(StashButton);
        });
    }
}
