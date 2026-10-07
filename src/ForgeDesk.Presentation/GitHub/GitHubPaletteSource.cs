using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>
/// GitHub entries of the command palette for the project on screen when it is linked to a GitHub
/// repository: open it on GitHub, jump to pull requests, issues and Actions, create an issue or a pull request.
/// </summary>
public sealed class GitHubPaletteSource : IPaletteSource
{
    private readonly INavigationService _navigation;
    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;

    public GitHubPaletteSource(INavigationService navigation, IShellIntegration shell, INotificationService notifications)
    {
        _navigation = navigation;
        _shell = shell;
        _notifications = notifications;
    }

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || workspace.GitHub is not { } repo
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.GitHub))
        {
            return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        }

        var items = new List<PaletteItem>();
        var subtitle = $"{workspace.Name} · {repo.FullName}";
        if (query.Wants(PaletteCategory.Action))
        {
            items.Add(new PaletteItem
            {
                Title = "Open repository on GitHub",
                Subtitle = repo.HtmlUrl,
                Icon = "Open20",
                Category = PaletteCategory.Action,
                Keywords = "github browser web repository remote",
                Boost = 1,
                Execute = () =>
                {
                    try
                    {
                        _shell.OpenUrl(repo.HtmlUrl);
                    }
                    catch (Exception ex)
                    {
                        _notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
                    }

                    return Task.CompletedTask;
                },
            });
            items.Add(Go(workspace, "New issue…", subtitle, "AddCircle20", PaletteCategory.Action, "github create issue bug report", GitHubNavigation.NewIssue()));
            items.Add(Go(workspace, "New pull request…", subtitle, "BranchRequest20", PaletteCategory.Action, "github create pull request pr review merge",
                GitHubNavigation.NewPullRequest()));
        }

        if (query.Wants(PaletteCategory.Navigation))
        {
            items.Add(Go(workspace, "Pull requests", subtitle, "BranchRequest20", PaletteCategory.Navigation, "github pr pull requests review", GitHubNavigation.PullRequests()));
            items.Add(Go(workspace, "Issues", subtitle, "Record20", PaletteCategory.Navigation, "github issues bugs", GitHubNavigation.Issues()));
            items.Add(Go(workspace, "Actions", subtitle, "Flow20", PaletteCategory.Navigation, "github actions ci workflows runs builds", GitHubNavigation.Actions()));
        }

        return Task.FromResult<IReadOnlyList<PaletteItem>>(items);
    }

    private static PaletteItem Go(ProjectWorkspaceViewModel workspace, string title, string subtitle, string icon, PaletteCategory category, string keywords,
        GitHubNavigation navigation) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Icon = icon,
        Category = category,
        Keywords = keywords,
        Execute = () => workspace.SelectSectionAsync(WorkspaceSection.GitHub, navigation),
    };
}
