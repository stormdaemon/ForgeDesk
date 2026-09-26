using System.Windows.Controls;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// Details of a commit or a stash: message, author, parents, changed files with line bars, and
/// the read-only diff of the selected file. Shared by History and Stashes.
/// </summary>
public partial class CommitDetailsView : UserControl
{
    public CommitDetailsView()
    {
        InitializeComponent();
    }
}
