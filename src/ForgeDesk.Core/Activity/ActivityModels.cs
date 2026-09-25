namespace ForgeDesk.Core.Activity;

public enum ActivityKind
{
    ProjectAdded = 0,
    ProjectRemoved,
    ProjectCloned,
    ProjectRelocated,

    GitCommit = 10,
    GitPush,
    GitPull,
    GitFetch,
    GitCheckout,
    GitBranchCreated,
    GitBranchDeleted,
    GitMerge,
    GitStash,
    GitDiscard,
    GitTagCreated,

    RunCompleted = 30,

    ReleasePublished = 40,
    ReleaseFailed,

    WorkItemCreated = 50,
    WorkItemCompleted,
    WorkItemUpdated,

    IssueCreated = 60,
    IssueCommented,
    IssueStateChanged,
    PullRequestCreated,
    WorkflowRerun,
    WorkflowCancelled,

    Error = 90,
}

public enum ActivityOutcome
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Failure = 3,
}

public sealed record ActivityEntry
{
    public long Id { get; init; }
    public string? ProjectId { get; init; }
    public DateTimeOffset At { get; init; }
    public ActivityKind Kind { get; init; }
    public ActivityOutcome Outcome { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>What the entry points to: "commit", "run", "work-item", "release", "url"…</summary>
    public string? RefKind { get; init; }
    public string? RefValue { get; init; }
}

public sealed record ActivityQuery
{
    public string? ProjectId { get; init; }
    public IReadOnlyList<ActivityKind>? Kinds { get; init; }
    public ActivityOutcome? Outcome { get; init; }
    public string? Search { get; init; }
    public DateTimeOffset? Before { get; init; }
    public int Limit { get; init; } = 200;
}
