using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>A branch in the header's branch selector.</summary>
public sealed class BranchItemViewModel
{
    public BranchItemViewModel(GitBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        Branch = branch;
    }

    public GitBranch Branch { get; }

    /// <summary>"main" for local branches, "origin/feature" for remote ones.</summary>
    public string Name => Branch.Name;

    public bool IsRemote => Branch.IsRemote;

    public bool IsCurrent => Branch.IsCurrent;

    public string? Upstream => Branch.Upstream;

    public int Ahead => Branch.Ahead;

    public int Behind => Branch.Behind;

    public bool HasAheadBehind => Branch.Ahead > 0 || Branch.Behind > 0;

    public DateTimeOffset? TipDate => Branch.TipDate;

    public string? TipSubject => Branch.TipSubject;

    /// <summary>Name of the local branch a remote branch is checked out as ("feature" for "origin/feature").</summary>
    public string LocalName => IsRemote && Branch.RemoteName is { Length: > 0 } remote && Name.StartsWith(remote + "/", StringComparison.Ordinal)
        ? Name[(remote.Length + 1)..]
        : Name;

    public string ToolTip
    {
        get
        {
            var tip = TipSubject is null ? Name : $"{Name}\n{TipSubject}";
            if (Branch.UpstreamGone)
            {
                return tip + "\nIts upstream branch was deleted on the remote.";
            }

            return Upstream is null || IsRemote ? tip : $"{tip}\nTracks {Upstream}";
        }
    }
}
