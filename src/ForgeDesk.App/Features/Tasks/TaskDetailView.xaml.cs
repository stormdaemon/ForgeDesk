using System.Windows.Controls;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>The details pane of a task (edits are saved by the view model; Esc closes the pane).</summary>
public partial class TaskDetailView : UserControl
{
    public TaskDetailView()
    {
        InitializeComponent();
    }
}
