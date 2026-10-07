using System.Windows.Controls;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>Overview of the GitHub tab: repository card, open pull request / issue counts, CI health and rate limit.</summary>
public partial class GitHubOverviewView : UserControl
{
    public GitHubOverviewView()
    {
        InitializeComponent();
    }
}
