using System.Windows.Controls;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>
/// The GitHub tab: Overview · Pull requests · Issues · Actions above one live view per opened
/// segment (lists keep their scroll and selection), or the state explaining why GitHub can't be
/// used for the project (not linked, signed out, turned off) with the action that fixes it.
/// </summary>
public partial class GitHubSectionView : UserControl
{
    public GitHubSectionView()
    {
        InitializeComponent();
    }
}
