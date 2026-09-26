using System.Windows.Controls;

namespace ForgeDesk.App.Features.Workspace;

/// <summary>
/// An open project: header (identity, branch, sync, "Open in"), banners, the tab strip and the
/// sections, one live view per opened tab so terminals and scroll positions survive tab switches.
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }
}
