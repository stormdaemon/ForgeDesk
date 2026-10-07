namespace ForgeDesk.Presentation.GitHub;

/// <summary>The views of the GitHub tab, in the order of its sub-navigation.</summary>
public enum GitHubView
{
    Overview,
    PullRequests,
    Issues,
    Actions,
}

/// <summary>
/// Deep link into the GitHub tab, passed as the argument of
/// <c>ProjectContext.RequestNavigation(WorkspaceSection.GitHub, …)</c> or
/// <c>INavigationService.OpenProjectAsync(id, WorkspaceSection.GitHub, …)</c>.
/// </summary>
public sealed record GitHubNavigation(GitHubView View)
{
    /// <summary>Pull requests / Issues: the number to select and show in the detail pane.</summary>
    public int? Number { get; init; }

    /// <summary>Actions: the workflow run to select and show with its jobs.</summary>
    public long? RunId { get; init; }

    /// <summary>Pull requests / Issues: open the "New …" dialog.</summary>
    public bool CreateNew { get; init; }

    public static GitHubNavigation Overview() => new(GitHubView.Overview);

    public static GitHubNavigation PullRequests() => new(GitHubView.PullRequests);

    public static GitHubNavigation PullRequest(int number) => new(GitHubView.PullRequests) { Number = number };

    public static GitHubNavigation NewPullRequest() => new(GitHubView.PullRequests) { CreateNew = true };

    public static GitHubNavigation Issues() => new(GitHubView.Issues);

    public static GitHubNavigation Issue(int number) => new(GitHubView.Issues) { Number = number };

    public static GitHubNavigation NewIssue() => new(GitHubView.Issues) { CreateNew = true };

    public static GitHubNavigation Actions() => new(GitHubView.Actions);

    public static GitHubNavigation Run(long runId) => new(GitHubView.Actions) { RunId = runId };

    /// <summary>Reads a navigation argument: a <see cref="GitHubNavigation"/> or a <see cref="GitHubView"/>; null otherwise.</summary>
    public static GitHubNavigation? From(object? argument) => argument switch
    {
        GitHubNavigation navigation => navigation,
        GitHubView view => new GitHubNavigation(view),
        _ => null,
    };
}
