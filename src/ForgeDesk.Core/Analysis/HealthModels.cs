using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Analysis;

public sealed record LanguageStat(string Language, int Files, long Lines, long Bytes, string Color);

public sealed record DependencyInfo(string Ecosystem, string Name, string? Version, bool IsDevelopment, string Manifest);

public sealed record ImportantFileCheck(string Label, bool Present, string? Path, string Why);

public sealed record TodoItem(string RelativePath, int Line, string Tag, string Text);

public sealed record LargeFile(string RelativePath, long Bytes);

public sealed record ContributorStat(string Name, int Commits);

public sealed record GitActivity
{
    public int CommitsLast30Days { get; init; }
    public int CommitsLast90Days { get; init; }
    public int TotalCommits { get; init; }
    public DateTimeOffset? FirstCommitAt { get; init; }
    public DateTimeOffset? LastCommitAt { get; init; }

    /// <summary>Commit counts for the last 12 weeks, oldest first.</summary>
    public IReadOnlyList<int> WeeklyCommits { get; init; } = [];
    public IReadOnlyList<ContributorStat> TopContributors { get; init; } = [];
}

public sealed record RepositoryState
{
    public int UncommittedChanges { get; init; }
    public int UnpushedCommits { get; init; }
    public int BehindCommits { get; init; }
    public int Stashes { get; init; }
    public int LocalBranches { get; init; }
    public IReadOnlyList<string> StaleBranches { get; init; } = [];
    public IReadOnlyList<string> MergedBranches { get; init; } = [];
    public bool HasUpstream { get; init; }
}

/// <summary>Actionable health report. Favors findings the developer can act on over vanity metrics.</summary>
public sealed record ProjectHealthReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public long TotalLines { get; init; }
    public bool ScanTruncated { get; init; }
    public IReadOnlyList<LanguageStat> Languages { get; init; } = [];
    public IReadOnlyList<DependencyInfo> Dependencies { get; init; } = [];
    public IReadOnlyList<ImportantFileCheck> ImportantFiles { get; init; } = [];
    public IReadOnlyList<TodoItem> Todos { get; init; } = [];
    public int TodoCount { get; init; }
    public IReadOnlyList<LargeFile> LargestFiles { get; init; } = [];
    public GitActivity? Git { get; init; }
    public RepositoryState? Repository { get; init; }
    public Detection.TestInfo? Tests { get; init; }
    public int TestFileCount { get; init; }
    public IReadOnlyList<AttentionReason> Attention { get; init; } = [];

    /// <summary>0–100 synthesis of the checks, for a single glanceable indicator.</summary>
    public int Score { get; init; }
}
