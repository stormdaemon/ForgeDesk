namespace ForgeDesk.Core.Analysis;

/// <summary>Bounds that keep an analysis fast and memory-safe on huge repositories.</summary>
internal static class AnalysisLimits
{
    /// <summary>Files listed (git ls-files or folder walk).</summary>
    public const int MaxListedFiles = 50_000;

    /// <summary>Files whose lines are counted; beyond it the report is marked truncated.</summary>
    public const int MaxCountedFiles = 30_000;

    /// <summary>Bytes read for line counting.</summary>
    public const long MaxBytesRead = 200L * 1024 * 1024;

    /// <summary>Larger source files are not read (they are almost always generated or data).</summary>
    public const long MaxSourceFileBytes = 1024 * 1024;

    public const int MaxTodoItems = 500;
    public const int MaxTodoTextLength = 200;
    public const int LargestFilesCount = 10;

    /// <summary>Committed files above this size deserve Git LFS or removal.</summary>
    public const long OversizedFileBytes = 10L * 1024 * 1024;

    public const int MaxDependencies = 500;
    public const int MaxManifests = 200;
    public const int MaxManifestBytes = 1024 * 1024;

    /// <summary>Commits read for the activity charts; totals beyond it are counted separately.</summary>
    public const int MaxCommits = 10_000;

    public const int TopContributors = 5;
    public static readonly TimeSpan StaleBranchAge = TimeSpan.FromDays(90);
}
