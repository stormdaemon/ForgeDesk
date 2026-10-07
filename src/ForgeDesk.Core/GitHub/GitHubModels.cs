namespace ForgeDesk.Core.GitHub;

public enum CiState
{
    /// <summary>Could not be determined (offline, not signed in…).</summary>
    Unknown,

    /// <summary>The repository has no workflow runs.</summary>
    None,
    Queued,
    Running,
    Success,
    Failure,
    Cancelled,
}

/// <summary>Condensed CI health for a branch: the latest run of each workflow.</summary>
public sealed record CiSummary
{
    public CiState State { get; init; }
    public string? Branch { get; init; }
    public string? HeadSha { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<WorkflowRunInfo> LatestRuns { get; init; } = [];
    public string? Message { get; init; }
}

public sealed record GitHubUser(string Login, string? Name, string AvatarUrl, string HtmlUrl);

public sealed record GitHubAccount(GitHubUser User, IReadOnlyList<string> Scopes, GitHubAuthMethod Method)
{
    /// <summary>
    /// False when the session was restored without reaching GitHub (offline at startup): the
    /// account comes from the cached login and the token has not been re-validated yet.
    /// </summary>
    public bool IsVerified { get; init; } = true;

    /// <summary>
    /// A user-facing note when the token lacks scopes ForgeDesk relies on (classic tokens
    /// without "repo" or "workflow"), or null when nothing is missing.
    /// </summary>
    public string? Warning { get; init; }
}

public enum GitHubAuthMethod
{
    PersonalAccessToken,
    GitCredentialManager,
    GitHubCli,
}

public sealed record GitHubRepository
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public string FullName => $"{Owner}/{Name}";
    public string? Description { get; init; }
    public required string HtmlUrl { get; init; }
    public required string CloneUrl { get; init; }
    public string? DefaultBranch { get; init; }
    public bool IsPrivate { get; init; }
    public bool IsFork { get; init; }
    public bool IsArchived { get; init; }
    public string? Language { get; init; }
    public int Stars { get; init; }
    public int Forks { get; init; }
    public int OpenIssues { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool CanPush { get; init; }
}

/// <param name="Color">Label color as "#rrggbb".</param>
public sealed record GitHubLabel(string Name, string Color);

public enum IssueStateFilter
{
    Open,
    Closed,
    All,
}

public sealed record GitHubIssue
{
    public int Number { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool IsOpen { get; init; }
    public required string Author { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public int Comments { get; init; }
    public IReadOnlyList<GitHubLabel> Labels { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public required string HtmlUrl { get; init; }
}

public sealed record GitHubComment(long Id, string Author, string? AuthorAvatarUrl, string Body, DateTimeOffset CreatedAt, string HtmlUrl);

public enum PullRequestState
{
    Open,
    Closed,
    Merged,
    Draft,
}

public sealed record GitHubPullRequest
{
    public int Number { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = string.Empty;
    public PullRequestState State { get; init; }
    public required string Author { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public string? HeadSha { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int Comments { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }
    public bool? Mergeable { get; init; }
    public IReadOnlyList<GitHubLabel> Labels { get; init; } = [];
    public IReadOnlyList<string> Reviewers { get; init; } = [];
    public required string HtmlUrl { get; init; }

    /// <summary>Combined status of checks on the head commit (filled by detail calls).</summary>
    public CiState Checks { get; init; } = CiState.Unknown;
}

public sealed record WorkflowInfo(long Id, string Name, string Path, string State, string HtmlUrl);

public sealed record WorkflowRunInfo
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public long WorkflowId { get; init; }
    public int RunNumber { get; init; }
    public int RunAttempt { get; init; }
    public required string Event { get; init; }
    public string? Branch { get; init; }
    public string? HeadSha { get; init; }

    /// <summary>Subject line of the head commit (or the run title when GitHub omits the commit).</summary>
    public string? CommitMessage { get; init; }
    public string? Actor { get; init; }
    public CiState State { get; init; }

    /// <summary>Raw GitHub status/conclusion (e.g. "completed"/"failure").</summary>
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required string HtmlUrl { get; init; }

    public TimeSpan? Duration => StartedAt is { } s && UpdatedAt is { } u && State is not CiState.Running and not CiState.Queued ? u - s : null;
}

public sealed record WorkflowStepInfo(int Number, string Name, CiState State, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

public sealed record WorkflowJobInfo(long Id, string Name, CiState State, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string HtmlUrl, IReadOnlyList<WorkflowStepInfo> Steps);

public sealed record GitHubReleaseAsset(long Id, string Name, long Size, int DownloadCount, string DownloadUrl, string ContentType)
{
    /// <summary>"uploaded" once the file is complete; an interrupted upload can leave another state (e.g. "starter").</summary>
    public string State { get; init; } = UploadedState;

    public const string UploadedState = "uploaded";

    public bool IsUploaded => string.Equals(State, UploadedState, StringComparison.OrdinalIgnoreCase);
}

public sealed record GitHubRelease
{
    public long Id { get; init; }
    public required string TagName { get; init; }
    public string? Name { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Author { get; init; }
    public required string HtmlUrl { get; init; }
    public string? TargetCommitish { get; init; }
    public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = [];
}

public sealed record GitHubTag(string Name, string Sha);

public sealed record NewRelease
{
    public required string TagName { get; init; }
    public required string Name { get; init; }
    public string Body { get; init; } = string.Empty;

    /// <summary>Branch or SHA used when the tag does not exist yet.</summary>
    public string? TargetCommitish { get; init; }
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }
    public bool MakeLatest { get; init; } = true;
}

/// <summary>Fields to change on an existing release; null leaves a field unchanged.</summary>
public sealed record ReleaseChanges
{
    public string? Name { get; init; }
    public string? Body { get; init; }

    /// <summary>Branch or SHA GitHub tags when the release is published and the tag does not exist yet.</summary>
    public string? TargetCommitish { get; init; }
    public bool? Prerelease { get; init; }
    public bool? MakeLatest { get; init; }
}

public sealed record RateLimitInfo(int Limit, int Remaining, DateTimeOffset ResetsAt);

/// <summary>Byte-level progress of an upload.</summary>
public sealed record TransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : (double)BytesTransferred / TotalBytes;
}
