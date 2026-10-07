using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Tests.Git.Support;

public static class GitData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public const string Head = "1111111111111111111111111111111111111111";

    public static GitStatus Status(string? branch = "main", string? upstream = "origin/main", int ahead = 0, int behind = 0,
        GitRepositoryState state = GitRepositoryState.Normal, int stashes = 0, bool unborn = false, params GitStatusEntry[] entries) => new()
    {
        Branch = branch,
        Upstream = upstream,
        Ahead = ahead,
        Behind = behind,
        HeadSha = unborn ? null : Head,
        IsDetached = branch is null,
        IsUnborn = unborn,
        State = state,
        StashCount = stashes,
        Entries = entries,
    };

    public static GitStatus WithEntries(params GitStatusEntry[] entries) => Status(entries: entries);

    public static GitStatusEntry Modified(string path) =>
        new() { Path = path, IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Modified };

    public static GitStatusEntry Staged(string path, GitFileState state = GitFileState.Modified) =>
        new() { Path = path, IndexState = state, WorkTreeState = GitFileState.Unmodified };

    /// <summary>Staged, then modified again: one row in each group.</summary>
    public static GitStatusEntry PartlyStaged(string path) =>
        new() { Path = path, IndexState = GitFileState.Modified, WorkTreeState = GitFileState.Modified };

    public static GitStatusEntry Untracked(string path) =>
        new() { Path = path, IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Untracked };

    public static GitStatusEntry Renamed(string oldPath, string path) =>
        new() { Path = path, OriginalPath = oldPath, IndexState = GitFileState.Renamed, WorkTreeState = GitFileState.Unmodified };

    public static GitStatusEntry Conflicted(string path) =>
        new() { Path = path, IndexState = GitFileState.Conflicted, WorkTreeState = GitFileState.Conflicted };

    public static GitCommit Commit(string sha, string subject, params string[] parents) => new()
    {
        Sha = sha,
        Subject = subject,
        Author = new GitSignature("Ada Lovelace", "ada@example.com", Now.AddHours(-1)),
        Committer = new GitSignature("Ada Lovelace", "ada@example.com", Now.AddHours(-1)),
        Parents = parents,
    };

    public static GitCommit Commit(string sha, string subject, IReadOnlyList<string> refs, params string[] parents) =>
        Commit(sha, subject, parents) with { Refs = refs };

    /// <summary>A linear history of <paramref name="count"/> commits, newest first, with 40-character SHAs.</summary>
    public static IReadOnlyList<GitCommit> Linear(int count, int offset = 0) =>
        Enumerable.Range(offset, count)
            .Select(i => Commit(Sha(i), $"Commit {i}", Sha(i + 1)))
            .ToList();

    public static string Sha(int index) => index.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    public static GitBranch Local(string name, bool current = false, string? upstream = null, bool gone = false, int ahead = 0, int behind = 0) => new()
    {
        Name = name,
        FullName = $"refs/heads/{name}",
        IsCurrent = current,
        Upstream = upstream,
        UpstreamGone = gone,
        Ahead = ahead,
        Behind = behind,
        TipSha = Head,
        TipDate = Now.AddDays(-1),
        TipSubject = $"Work on {name}",
        TipAuthor = "Ada Lovelace",
    };

    public static GitBranch Remote(string name) => new()
    {
        Name = name,
        FullName = $"refs/remotes/{name}",
        IsRemote = true,
        RemoteName = name[..name.IndexOf('/', StringComparison.Ordinal)],
        TipSha = Head,
        TipDate = Now.AddDays(-2),
    };

    public static FileDiff Diff(string path, params string[] addedLines) => new()
    {
        Path = path,
        HeaderLines = [$"diff --git a/{path} b/{path}"],
        Hunks =
        [
            new DiffHunk
            {
                Header = "@@ -1,1 +1,2 @@",
                OldStart = 1,
                OldCount = 1,
                NewStart = 1,
                NewCount = 1 + addedLines.Length,
                Lines = [new DiffLine(DiffLineKind.Context, "first", 1, 1), .. addedLines.Select((l, i) => new DiffLine(DiffLineKind.Added, l, null, 2 + i))],
            },
        ],
    };
}
