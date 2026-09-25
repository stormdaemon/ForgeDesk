using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Releases;

public sealed record VersionSuggestion(string Label, string Version, string Reason);

/// <summary>Everything the release wizard needs to pre-fill its form.</summary>
public sealed record ReleaseContext
{
    public string? LatestTag { get; init; }
    public string? LatestVersion { get; init; }

    /// <summary>Prefix the project's tags use ("v" in v1.2.0, "" in 1.2.0): tag name = prefix + version.</summary>
    public string TagPrefix { get; init; } = "v";

    /// <summary>Candidate versions, the recommended one first.</summary>
    public IReadOnlyList<VersionSuggestion> Suggestions { get; init; } = [];
    public IReadOnlyList<GitCommit> CommitsSinceLatest { get; init; } = [];
    public string DraftNotes { get; init; } = string.Empty;
    public string? CurrentBranch { get; init; }
    public string? DefaultBranch { get; init; }
    public string? HeadSha { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public int UnpushedCommits { get; init; }

    /// <summary>Blocking problems (not signed in, no GitHub remote…) and warnings to display.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Files that look like release artifacts (dist/*.zip, bin/Release/*.exe…).</summary>
    public IReadOnlyList<string> SuggestedAssets { get; init; } = [];
}

public sealed record ReleasePlan
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;

    /// <summary>Commit to tag (defaults to HEAD).</summary>
    public string? Target { get; init; }
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }

    /// <summary>Absolute paths of files to upload as release assets.</summary>
    public IReadOnlyList<string> Assets { get; init; } = [];

    /// <summary>Create the annotated tag locally and push it before creating the release.</summary>
    public bool CreateAndPushTag { get; init; } = true;
}

public enum ReleaseStepKind
{
    Validate,
    CreateTag,
    PushTag,
    CreateRelease,
    UploadAsset,
    Publish,
}

public enum ReleaseStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed record ReleaseStepUpdate(ReleaseStepKind Step, ReleaseStepState State, string Title, string? Detail = null, double? Progress = null);

public sealed record ReleaseResult(bool Succeeded, GitHub.GitHubRelease? Release, IReadOnlyList<ReleaseStepUpdate> Steps, string? Error);
