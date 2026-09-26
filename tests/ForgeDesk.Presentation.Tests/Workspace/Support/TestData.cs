using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Presentation.Tests.Workspace.Support;

public static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public static Project Project(string name, string path, bool pinned = false, DateTimeOffset? lastOpened = null, string? id = null,
        GitHubRepoRef? gitHub = null, int sortOrder = 0, DateTimeOffset? added = null) => new()
    {
        Id = id ?? Ids.New(),
        Name = name,
        Path = path,
        IsPinned = pinned,
        LastOpenedAt = lastOpened,
        GitHub = gitHub,
        SortOrder = sortOrder,
        AddedAt = added ?? Now.AddDays(-30),
    };

    public static GitStatus Status(string? branch = "main", string? upstream = "origin/main", int ahead = 0, int behind = 0,
        string head = "1111111111111111111111111111111111111111", params GitStatusEntry[] entries) => new()
    {
        Branch = branch,
        Upstream = upstream,
        Ahead = ahead,
        Behind = behind,
        HeadSha = head,
        IsDetached = branch is null,
        Entries = entries,
    };

    public static GitStatusEntry Modified(string path) =>
        new() { Path = path, IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Modified };

    public static GitStatusEntry Conflicted(string path) =>
        new() { Path = path, IndexState = GitFileState.Conflicted, WorkTreeState = GitFileState.Conflicted };

    public static GitBranch LocalBranch(string name, bool current = false, string? upstream = null, DateTimeOffset? tip = null) => new()
    {
        Name = name,
        FullName = $"refs/heads/{name}",
        IsCurrent = current,
        Upstream = upstream,
        TipDate = tip,
    };

    public static GitBranch RemoteBranch(string name, DateTimeOffset? tip = null) => new()
    {
        Name = name,
        FullName = $"refs/remotes/{name}",
        IsRemote = true,
        RemoteName = name[..name.IndexOf('/', StringComparison.Ordinal)],
        TipDate = tip,
    };
}
