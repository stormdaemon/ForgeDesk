using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ForgeDesk.App.Features.Git;

/// <summary>"Create tag": validated name and optional message. Focuses the name when shown.</summary>
public partial class CreateTagDialogView : UserControl
{
    public CreateTagDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
        });
    }
}
