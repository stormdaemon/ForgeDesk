using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Git;

/// <summary>A row of the Branches list: a group header ("Local branches", "origin") or a branch.</summary>
public abstract class BranchListRow : ObservableObject
{
    public abstract bool IsHeader { get; }
}

public sealed class BranchGroupHeader : BranchListRow
{
    public BranchGroupHeader(string title, int count, bool isRemote)
    {
        Title = title;
        Count = count;
        IsRemote = isRemote;
    }

    public string Title { get; }

    public int Count { get; }

    public bool IsRemote { get; }

    public override bool IsHeader => true;
}

/// <summary>A local or remote branch with its upstream state, merge state and tip commit.</summary>
public sealed class BranchRowViewModel : BranchListRow
{
    public BranchRowViewModel(GitBranch branch, bool isMerged = false, bool isDefault = false)
    {
        ArgumentNullException.ThrowIfNull(branch);
        Branch = branch;
        IsDefault = isDefault;
        IsMerged = isMerged && !branch.IsRemote && !branch.IsCurrent && !isDefault;
    }

    public GitBranch Branch { get; }

    public override bool IsHeader => false;

    /// <summary>"main", or "origin/feature" for a remote branch.</summary>
    public string Name => Branch.Name;

    public string FullName => Branch.FullName;

    /// <summary>The name without the remote ("feature" for "origin/feature").</summary>
    public string DisplayName => IsRemote ? LocalName : Name;

    public bool IsRemote => Branch.IsRemote;

    public bool IsLocal => !Branch.IsRemote;

    public bool IsCurrent => Branch.IsCurrent;

    /// <summary>The repository's default branch (never offered for bulk deletion).</summary>
    public bool IsDefault { get; }

    /// <summary>Fully merged into the default branch.</summary>
    public bool IsMerged { get; }

    public string? RemoteName => Branch.RemoteName;

    public string? Upstream => Branch.Upstream;

    public bool HasUpstream => IsLocal && Branch.Upstream is not null;

    /// <summary>The upstream branch was deleted on the remote.</summary>
    public bool IsGone => IsLocal && Branch.UpstreamGone;

    public bool NeedsPublish => IsLocal && Branch.Upstream is null;

    public int Ahead => Branch.Ahead;

    public int Behind => Branch.Behind;

    /// <summary>A local branch other than the current one: can be switched to and deleted.</summary>
    public bool IsOtherLocal => IsLocal && !IsCurrent;

    /// <summary>"Switch to branch", or "Check out as local branch" for a remote branch.</summary>
    public string SwitchText => IsRemote ? "Check out as local branch" : "Switch to branch";

    public bool CanSwitch => !IsCurrent;

    public bool HasAhead => IsLocal && Ahead > 0;

    public bool HasBehind => IsLocal && Behind > 0;

    public string AheadText => $"↑{Ahead}";

    public string BehindText => $"↓{Behind}";

    public string? TipSubject => Branch.TipSubject is { Length: > 0 } subject ? subject : null;

    public DateTimeOffset? TipDate => Branch.TipDate;

    public string? TipAuthor => Branch.TipAuthor;

    public string? TipShortSha => Branch.TipSha is { Length: > 0 } sha ? sha[..Math.Min(7, sha.Length)] : null;

    /// <summary>Name of the local branch a remote branch is checked out as.</summary>
    public string LocalName => IsRemote && RemoteName is { Length: > 0 } remote && Name.StartsWith(remote + "/", StringComparison.Ordinal)
        ? Name[(remote.Length + 1)..]
        : Name;

    /// <summary>"Tracks origin/main · 2 ahead, 1 behind", "Not published", "Upstream deleted".</summary>
    public string UpstreamText
    {
        get
        {
            if (IsRemote)
            {
                return $"Remote branch on {RemoteName ?? "the remote"}";
            }

            if (Branch.Upstream is null)
            {
                return "Not published: exists only on this computer";
            }

            if (IsGone)
            {
                return $"Its upstream {Branch.Upstream} was deleted on the remote";
            }

            var sync = (Ahead, Behind) switch
            {
                (0, 0) => "up to date",
                (> 0, 0) => $"{Ahead} to push",
                (0, > 0) => $"{Behind} to pull",
                _ => $"{Ahead} to push, {Behind} to pull",
            };
            return $"Tracks {Branch.Upstream} · {sync}";
        }
    }

    public string ToolTip
    {
        get
        {
            var tip = TipSubject is null ? Name : $"{Name}\n{TipSubject}";
            return $"{tip}\n{UpstreamText}";
        }
    }
}
