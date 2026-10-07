using System.Windows.Controls;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// The Git tab: the Changes · History · Branches · Stashes · Tags segments above one live view per
/// opened segment (so lists keep their scroll and selection), or the state of a folder that is not
/// a usable repository.
/// </summary>
public partial class GitSectionView : UserControl
{
    public GitSectionView()
    {
        InitializeComponent();
    }
}
