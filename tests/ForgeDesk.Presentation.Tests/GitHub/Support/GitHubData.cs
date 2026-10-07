using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Tests.Workspace.Support;

namespace ForgeDesk.Presentation.Tests.GitHub.Support;

/// <summary>GitHub records for tests.</summary>
public static class GitHubData
{
    public static DateTimeOffset Now => TestData.Now;

    public static GitHubRepository Repository(string defaultBranch = "main") => new()
    {
        Owner = "acme",
        Name = "forge-app",
        Description = "The forge app",
        HtmlUrl = "https://github.com/acme/forge-app",
        CloneUrl = "https://github.com/acme/forge-app.git",
        DefaultBranch = defaultBranch,
        Stars = 42,
        Forks = 3,
        OpenIssues = 7,
        PushedAt = Now.AddHours(-2),
    };

    public static GitHubPullRequest PullRequest(int number, string title = "Add login", PullRequestState state = PullRequestState.Open,
        string head = "feature/login", string baseBranch = "main", CiState checks = CiState.Unknown) => new()
    {
        Number = number,
        Title = title,
        State = state,
        Author = "ada",
        HeadBranch = head,
        BaseBranch = baseBranch,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddHours(-1),
        HtmlUrl = $"https://github.com/acme/forge-app/pull/{number}",
        Checks = checks,
        Labels = [new GitHubLabel("enhancement", "#a2eeef")],
    };

    public static GitHubIssue Issue(int number, string title = "Crash on start", bool open = true, string body = "It crashes.") => new()
    {
        Number = number,
        Title = title,
        Body = body,
        IsOpen = open,
        Author = "grace",
        CreatedAt = Now.AddDays(-2),
        UpdatedAt = Now.AddHours(-3),
        HtmlUrl = $"https://github.com/acme/forge-app/issues/{number}",
        Labels = [new GitHubLabel("bug", "d73a4a")],
    };

    public static GitHubComment Comment(long id, string body, string author = "linus") =>
        new(id, author, null, body, Now.AddHours(-id), $"https://github.com/acme/forge-app/issues/1#issuecomment-{id}");

    public static WorkflowRunInfo Run(long id, CiState state = CiState.Success, string name = "CI", long workflowId = 1, string branch = "main") => new()
    {
        Id = id,
        Name = name,
        WorkflowId = workflowId,
        RunNumber = (int)id,
        RunAttempt = 1,
        Event = "push",
        Branch = branch,
        HeadSha = "abcdef1234567890",
        CommitMessage = $"Commit for run {id}",
        Actor = "ada",
        State = state,
        CreatedAt = Now.AddMinutes(-id),
        StartedAt = Now.AddMinutes(-id),
        UpdatedAt = Now.AddMinutes(-id + 2),
        HtmlUrl = $"https://github.com/acme/forge-app/actions/runs/{id}",
    };

    public static WorkflowInfo Workflow(long id, string name) => new(id, name, $".github/workflows/{name.ToLowerInvariant()}.yml", "active", $"https://github.com/acme/forge-app/actions/workflows/{id}");

    public static WorkflowJobInfo Job(long id, string name, CiState state) =>
        new(id, name, state, Now.AddMinutes(-5), Now.AddMinutes(-3), $"https://github.com/acme/forge-app/actions/runs/1/job/{id}",
            [new WorkflowStepInfo(1, "Set up job", CiState.Success, Now.AddMinutes(-5), Now.AddMinutes(-4)), new WorkflowStepInfo(2, "Build", state, Now.AddMinutes(-4), Now.AddMinutes(-3))]);

    public static GitHubRelease Release(long id, string tag, bool draft = false, bool prerelease = false, DateTimeOffset? published = null) => new()
    {
        Id = id,
        TagName = tag,
        Name = $"Forge {tag}",
        Body = $"Notes of {tag}",
        IsDraft = draft,
        IsPrerelease = prerelease,
        CreatedAt = published ?? Now.AddDays(-id),
        PublishedAt = draft ? null : published ?? Now.AddDays(-id),
        Author = "ada",
        HtmlUrl = $"https://github.com/acme/forge-app/releases/tag/{tag}",
        Assets = [new GitHubReleaseAsset(id * 10, "forge.zip", 1024 * 1024, 12, $"https://github.com/acme/forge-app/releases/download/{tag}/forge.zip", "application/zip")],
    };
}
