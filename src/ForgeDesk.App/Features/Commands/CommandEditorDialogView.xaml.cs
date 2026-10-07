using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ForgeDesk.App.Features.Commands;

/// <summary>"Add command" / "Edit command". Focuses the name when shown.</summary>
public partial class CommandEditorDialogView : UserControl
{
    public CommandEditorDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
        });
    }
}
