using System.Text.RegularExpressions;
using Octokit;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Converts Octokit models to ForgeDesk records. Octokit models are not null-annotated and
/// GitHub omits fields for deleted users ("ghost") or old objects, so every access is defensive.
/// Enum-like values are read through <c>StringValue</c>: Octokit throws on values it doesn't know.
/// </summary>
internal static partial class GitHubMapper
{
    private const string DefaultLabelColor = "#ededed";
    private const string GhostLogin = "ghost";

    public static GitHubUser ToUser(User user) => new(
        user.Login,
        string.IsNullOrWhiteSpace(user.Name) ? null : user.Name,
        user.AvatarUrl ?? string.Empty,
        string.IsNullOrEmpty(user.HtmlUrl) ? $"https://github.com/{user.Login}" : user.HtmlUrl);

    public static GitHubRepository ToRepository(Repository repository)
    {
        var owner = repository.Owner?.Login;
        if (string.IsNullOrEmpty(owner) && repository.FullName?.Split('/') is [var fromFullName, _])
        {
            owner = fromFullName;
        }

        var permissions = repository.Permissions;
        return new GitHubRepository
        {
            Owner = owner ?? string.Empty,
            Name = repository.Name,
            Description = string.IsNullOrWhiteSpace(repository.Description) ? null : repository.Description,
            HtmlUrl = repository.HtmlUrl,
            CloneUrl = repository.CloneUrl,
            DefaultBranch = repository.DefaultBranch,
            IsPrivate = repository.Private,
            IsFork = repository.Fork,
            IsArchived = repository.Archived,
            Language = repository.Language,
            Stars = repository.StargazersCount,
            Forks = repository.ForksCount,
            OpenIssues = repository.OpenIssuesCount,
            PushedAt = repository.PushedAt,
            UpdatedAt = NullIfDefault(repository.UpdatedAt),
            CanPush = permissions is not null && (permissions.Push || permissions.Maintain || permissions.Admin),
        };
    }

    public static GitHubIssue ToIssue(Issue issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title ?? string.Empty,
        Body = issue.Body ?? string.Empty,
        IsOpen = string.Equals(issue.State.StringValue, "open", StringComparison.OrdinalIgnoreCase),
        Author = issue.User?.Login ?? GhostLogin,
        AuthorAvatarUrl = issue.User?.AvatarUrl,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ClosedAt = issue.ClosedAt,
        Comments = issue.Comments,
        Labels = ToLabels(issue.Labels),
        Assignees = issue.Assignees?.Select(a => a.Login).ToList() ?? (IReadOnlyList<string>)[],
        HtmlUrl = issue.HtmlUrl,
    };

    public static GitHubComment ToComment(IssueComment comment) => new(
        comment.Id,
        comment.User?.Login ?? GhostLogin,
        comment.User?.AvatarUrl,
        comment.Body ?? string.Empty,
        comment.CreatedAt,
        comment.HtmlUrl);

    public static GitHubPullRequest ToPullRequest(PullRequest pr, CiState checks = CiState.Unknown)
    {
        var reviewers = new List<string>();
        reviewers.AddRange(pr.RequestedReviewers?.Select(r => r.Login) ?? []);
        reviewers.AddRange(pr.RequestedTeams?.Select(t => t.Name) ?? []);

        return new GitHubPullRequest
        {
            Number = pr.Number,
            Title = pr.Title ?? string.Empty,
            Body = pr.Body ?? string.Empty,
            State = ToState(pr),
            Author = pr.User?.Login ?? GhostLogin,
            AuthorAvatarUrl = pr.User?.AvatarUrl,
            HeadBranch = pr.Head?.Ref ?? string.Empty,
            BaseBranch = pr.Base?.Ref ?? string.Empty,
            HeadSha = pr.Head?.Sha,
            CreatedAt = pr.CreatedAt,
            UpdatedAt = NullIfDefault(pr.UpdatedAt),
            Comments = pr.Comments,
            Additions = pr.Additions,
            Deletions = pr.Deletions,
            ChangedFiles = pr.ChangedFiles,
            Mergeable = pr.Mergeable,
            Labels = ToLabels(pr.Labels),
            Reviewers = reviewers,
            HtmlUrl = pr.HtmlUrl,
            Checks = checks,
        };
    }

    public static PullRequestState ToState(PullRequest pr)
    {
        if (pr.Merged || pr.MergedAt is not null)
        {
            return PullRequestState.Merged;
        }

        if (string.Equals(pr.State.StringValue, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestState.Closed;
        }

        return pr.Draft ? PullRequestState.Draft : PullRequestState.Open;
    }

    public static IReadOnlyList<GitHubLabel> ToLabels(IReadOnlyList<Label>? labels) =>
        labels?.Select(l => new GitHubLabel(l.Name, NormalizeColor(l.Color))).ToList() ?? [];

    /// <summary>GitHub sends label colors as bare hex ("d73a4a"); the UI wants "#d73a4a".</summary>
    public static string NormalizeColor(string? color)
    {
        var hex = color?.Trim().TrimStart('#') ?? string.Empty;
        return HexColor().IsMatch(hex) ? "#" + hex.ToLowerInvariant() : DefaultLabelColor;
    }

    public static WorkflowInfo ToWorkflow(Workflow workflow) =>
        new(workflow.Id, workflow.Name, workflow.Path, workflow.State.StringValue ?? string.Empty, workflow.HtmlUrl);

    public static WorkflowRunInfo ToWorkflowRun(WorkflowRun run)
    {
        var status = run.Status.StringValue;
        var conclusion = run.Conclusion?.StringValue;
        return new WorkflowRunInfo
        {
            Id = run.Id,
            Name = string.IsNullOrWhiteSpace(run.Name) ? run.Path ?? $"Workflow {run.WorkflowId}" : run.Name,
            WorkflowId = run.WorkflowId,
            RunNumber = (int)run.RunNumber,
            RunAttempt = (int)Math.Max(1, run.RunAttempt),
            Event = run.Event ?? string.Empty,
            Branch = run.HeadBranch,
            HeadSha = run.HeadSha,
            CommitMessage = FirstLine(run.HeadCommit?.Message) ?? (string.IsNullOrWhiteSpace(run.DisplayTitle) ? null : run.DisplayTitle),
            Actor = run.Actor?.Login ?? run.TriggeringActor?.Login,
            State = CiStates.FromStatus(status, conclusion),
            Status = status,
            Conclusion = conclusion,
            CreatedAt = run.CreatedAt,
            UpdatedAt = NullIfDefault(run.UpdatedAt),
            StartedAt = NullIfDefault(run.RunStartedAt),
            HtmlUrl = run.HtmlUrl,
        };
    }

    public static WorkflowJobInfo ToWorkflowJob(WorkflowJob job) => new(
        job.Id,
        job.Name,
        CiStates.FromStatus(job.Status.StringValue, job.Conclusion?.StringValue),
        NullIfDefault(job.StartedAt),
        job.CompletedAt,
        job.HtmlUrl,
        job.Steps?
            .OrderBy(s => s.Number)
            .Select(s => new WorkflowStepInfo(
                s.Number,
                s.Name,
                CiStates.FromStatus(s.Status.StringValue, s.Conclusion?.StringValue),
                s.StartedAt,
                s.CompletedAt))
            .ToList() ?? []);

    public static GitHubRelease ToRelease(Release release) => new()
    {
        Id = release.Id,
        TagName = release.TagName,
        Name = string.IsNullOrWhiteSpace(release.Name) ? null : release.Name,
        Body = release.Body ?? string.Empty,
        IsDraft = release.Draft,
        IsPrerelease = release.Prerelease,
        CreatedAt = release.CreatedAt,
        PublishedAt = release.PublishedAt,
        Author = release.Author?.Login,
        HtmlUrl = release.HtmlUrl,
        TargetCommitish = release.TargetCommitish,
        Assets = release.Assets?.Select(ToAsset).ToList() ?? [],
    };

    public static GitHubReleaseAsset ToAsset(ReleaseAsset asset) => new(
        asset.Id,
        asset.Name,
        asset.Size,
        asset.DownloadCount,
        asset.BrowserDownloadUrl,
        asset.ContentType ?? "application/octet-stream")
    {
        State = string.IsNullOrEmpty(asset.State) ? GitHubReleaseAsset.UploadedState : asset.State,
    };

    public static GitHubTag ToTag(RepositoryTag tag) => new(tag.Name, tag.Commit?.Sha ?? string.Empty);

    public static RateLimitInfo ToRateLimit(RateLimit rate) => new(rate.Limit, rate.Remaining, rate.Reset);

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return (end < 0 ? trimmed : trimmed[..end]).TrimEnd();
    }

    private static DateTimeOffset? NullIfDefault(DateTimeOffset value) => value == default ? null : value;

    [GeneratedRegex("^[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
