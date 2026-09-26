namespace ForgeDesk.Core.Git;

public enum GitFileState
{
    Unmodified,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Ignored,
    Conflicted,
}

public enum GitRepositoryState
{
    Normal,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
    Bisecting,
}

/// <summary>One path in <c>git status</c>. A file can be both staged and unstaged.</summary>
public sealed record GitStatusEntry
{
    /// <summary>Path relative to the repository root, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>Previous path for renames/copies.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>State in the index (staged side).</summary>
    public GitFileState IndexState { get; init; }

    /// <summary>State in the working tree (unstaged side).</summary>
    public GitFileState WorkTreeState { get; init; }

    public bool IsConflicted => IndexState == GitFileState.Conflicted || WorkTreeState == GitFileState.Conflicted;
    public bool IsUntracked => WorkTreeState == GitFileState.Untracked;
    public bool HasStagedChanges => !IsConflicted && !IsUntracked && IndexState is not GitFileState.Unmodified;
    public bool HasUnstagedChanges => IsUntracked || IsConflicted || WorkTreeState is not GitFileState.Unmodified;
}

public sealed record GitStatus
{
    /// <summary>
    /// Current branch name, or null when HEAD is detached. An unborn branch (repository without
    /// commits yet) still reports its name, with <see cref="IsUnborn"/> set.
    /// </summary>
    public string? Branch { get; init; }
    public string? HeadSha { get; init; }
    public bool IsDetached { get; init; }
    public bool IsUnborn { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public GitRepositoryState State { get; init; }
    public int StashCount { get; init; }
    public IReadOnlyList<GitStatusEntry> Entries { get; init; } = [];

    public IEnumerable<GitStatusEntry> Staged => Entries.Where(e => e.HasStagedChanges);
    public IEnumerable<GitStatusEntry> Unstaged => Entries.Where(e => e.HasUnstagedChanges && !e.IsConflicted);
    public IEnumerable<GitStatusEntry> Conflicted => Entries.Where(e => e.IsConflicted);
    public bool IsClean => Entries.Count == 0;
}

public sealed record GitSignature(string Name, string Email, DateTimeOffset When);

public sealed record GitCommit
{
    public required string Sha { get; init; }
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public required string Subject { get; init; }
    public string Body { get; init; } = string.Empty;
    public required GitSignature Author { get; init; }
    public GitSignature? Committer { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = [];

    /// <summary>Decorations: branch and tag names pointing at this commit ("HEAD -> main", "tag: v1.0").</summary>
    public IReadOnlyList<string> Refs { get; init; } = [];

    public bool IsMerge => Parents.Count > 1;
}

public sealed record GitFileChange(string Path, string? OldPath, GitFileState State, int Additions, int Deletions, bool IsBinary);

public sealed record GitCommitDetails(GitCommit Commit, IReadOnlyList<GitFileChange> Files);

public sealed record GitBranch
{
    /// <summary>Short name ("main", "origin/main").</summary>
    public required string Name { get; init; }

    /// <summary>Full ref ("refs/heads/main").</summary>
    public required string FullName { get; init; }

    public bool IsRemote { get; init; }
    public bool IsCurrent { get; init; }
    public string? RemoteName { get; init; }
    public string? Upstream { get; init; }
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string? TipSha { get; init; }
    public DateTimeOffset? TipDate { get; init; }
    public string? TipSubject { get; init; }
    public string? TipAuthor { get; init; }
}

public sealed record GitTag(string Name, string TargetSha, DateTimeOffset? Date, string? Message, bool IsAnnotated);

public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl);

public sealed record GitStash(int Index, string Name, string Message, DateTimeOffset? Date);

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    NoNewlineMarker,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber)
{
    /// <summary>
    /// True when the line ends with CR LF in the file. <see cref="Text"/> never contains the CR, so it
    /// can be displayed as is; the flag lets patches rebuilt from the diff reproduce the file exactly.
    /// </summary>
    public bool HasCarriageReturn { get; init; }
}

public sealed record DiffHunk
{
    public required string Header { get; init; }
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}

public sealed record FileDiff
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public bool IsBinary { get; init; }
    public bool IsNewFile { get; init; }
    public bool IsDeletedFile { get; init; }

    /// <summary>True when the diff exceeded the size limit and hunks were not loaded.</summary>
    public bool IsTooLarge { get; init; }

    /// <summary>The raw file header lines ("diff --git…", "index…", "---", "+++"), needed to build patches.</summary>
    public IReadOnlyList<string> HeaderLines { get; init; } = [];

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = [];
    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
}

public enum DiffTarget
{
    /// <summary>Working tree vs index (unstaged changes; untracked files shown as additions).</summary>
    WorkingTree,

    /// <summary>Index vs HEAD (staged changes).</summary>
    Staged,
}

/// <summary>Progress reported by long git operations (clone, fetch, push, pull).</summary>
public sealed record GitProgress(string Stage, int? Percent, string Message);

public sealed record GitInstallation(string ExecutablePath, string Version);

public sealed record GitIdentity(string? Name, string? Email);

public sealed record GitLogQuery
{
    public int Skip { get; init; }
    public int Take { get; init; } = 200;

    /// <summary>Revision or branch to walk ("HEAD" by default).</summary>
    public string? Revision { get; init; }

    public bool AllBranches { get; init; }
    public string? PathFilter { get; init; }

    /// <summary>Free text matched against message (--grep) and author.</summary>
    public string? Search { get; init; }
}

public sealed record GitCommitOptions(string Message, bool Amend = false, bool StageAll = false);

public sealed record GitPushOptions(bool SetUpstream = true, bool Force = false, bool Tags = false, string? Remote = null, string? Branch = null);
