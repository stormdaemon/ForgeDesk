using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

public enum RefPillKind
{
    /// <summary>Detached HEAD.</summary>
    Head,

    /// <summary>The checked-out branch ("HEAD -> main").</summary>
    CurrentBranch,

    Branch,
    RemoteBranch,
    Tag,
}

/// <summary>A branch or tag pointing at a commit, shown as a pill in History.</summary>
public sealed record RefPill(string Name, RefPillKind Kind)
{
    public bool IsCurrent => Kind == RefPillKind.CurrentBranch;

    public string ToolTip => Kind switch
    {
        RefPillKind.Head => "HEAD (detached): the commit checked out",
        RefPillKind.CurrentBranch => $"{Name} (current branch)",
        RefPillKind.RemoteBranch => $"{Name} (remote branch)",
        RefPillKind.Tag => $"Tag {Name}",
        _ => $"{Name} (branch)",
    };

    /// <summary>
    /// Reads git decorations ("HEAD -> main", "origin/main", "tag: v1.0", "HEAD"). Remote-tracking
    /// branches are recognized by their remote prefix; "origin/HEAD" pointers are skipped.
    /// </summary>
    public static IReadOnlyList<RefPill> Parse(IReadOnlyList<string> decorations, IReadOnlyCollection<string> remotes)
    {
        ArgumentNullException.ThrowIfNull(decorations);
        ArgumentNullException.ThrowIfNull(remotes);
        var pills = new List<RefPill>(decorations.Count);
        foreach (var raw in decorations)
        {
            var decoration = raw.Trim();
            if (decoration.Length == 0)
            {
                continue;
            }

            if (decoration.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                pills.Add(new RefPill(decoration["HEAD -> ".Length..], RefPillKind.CurrentBranch));
            }
            else if (decoration == "HEAD")
            {
                pills.Add(new RefPill("HEAD", RefPillKind.Head));
            }
            else if (decoration.StartsWith("tag: ", StringComparison.Ordinal))
            {
                pills.Add(new RefPill(decoration["tag: ".Length..], RefPillKind.Tag));
            }
            else if (decoration.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }
            else if (IsRemote(decoration, remotes))
            {
                pills.Add(new RefPill(decoration, RefPillKind.RemoteBranch));
            }
            else
            {
                pills.Add(new RefPill(decoration, RefPillKind.Branch));
            }
        }

        // Current branch and HEAD first, then local branches, remote branches and tags.
        return pills.OrderBy(p => p.Kind switch
        {
            RefPillKind.CurrentBranch or RefPillKind.Head => 0,
            RefPillKind.Branch => 1,
            RefPillKind.RemoteBranch => 2,
            _ => 3,
        }).ToList();
    }

    private static bool IsRemote(string name, IReadOnlyCollection<string> remotes)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        var prefix = name[..slash];
        return remotes.Count == 0 ? prefix == "origin" || prefix == "upstream" : remotes.Contains(prefix);
    }
}

/// <summary>A commit of the History list: graph cell, subject, refs, author, date and short SHA.</summary>
public sealed class CommitRowViewModel
{
    public CommitRowViewModel(GitCommit commit, CommitGraphRow graph, IReadOnlyList<RefPill> refs)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Commit = commit;
        Graph = graph ?? CommitGraphRow.Empty;
        Refs = refs ?? [];
    }

    public GitCommit Commit { get; }

    public CommitGraphRow Graph { get; }

    public IReadOnlyList<RefPill> Refs { get; }

    public string Sha => Commit.Sha;

    public string ShortSha => Commit.ShortSha;

    public string Subject => Commit.Subject.Length == 0 ? "(no message)" : Commit.Subject;

    public string AuthorName => Commit.Author.Name;

    public string AuthorEmail => Commit.Author.Email;

    public DateTimeOffset Date => Commit.Author.When;

    /// <summary>Full local date for the tooltip of the relative date.</summary>
    public string FullDate => Format.Timestamp(Commit.Author.When);

    public bool HasRefs => Refs.Count > 0;

    public bool IsHead => Refs.Any(r => r.Kind is RefPillKind.CurrentBranch or RefPillKind.Head);

    public bool IsMerge => Commit.IsMerge;

    public string AuthorToolTip => $"{AuthorName} <{AuthorEmail}>";

    internal bool SameAs(CommitRowViewModel other) =>
        string.Equals(Sha, other.Sha, StringComparison.Ordinal) && Graph.Equals(other.Graph) && Refs.SequenceEqual(other.Refs)
        && string.Equals(Commit.Subject, other.Commit.Subject, StringComparison.Ordinal);
}

/// <summary>A file changed by a commit, with its share of the commit's line changes for the bars.</summary>
public sealed class CommitFileViewModel
{
    /// <summary>Width in pixels of a full change bar.</summary>
    public const double BarWidth = 40;

    public CommitFileViewModel(GitFileChange change, int largestChange)
    {
        ArgumentNullException.ThrowIfNull(change);
        Change = change;
        var scale = largestChange <= 0 ? 0 : BarWidth / largestChange;
        AdditionsBarWidth = change.Additions == 0 ? 0 : Math.Max(2, Math.Round(change.Additions * scale));
        DeletionsBarWidth = change.Deletions == 0 ? 0 : Math.Max(2, Math.Round(change.Deletions * scale));
    }

    public GitFileChange Change { get; }

    public string Path => Change.Path;

    public string? OldPath => Change.OldPath;

    public bool HasOldPath => OldPath is not null && !string.Equals(OldPath, Path, StringComparison.Ordinal);

    public string FileName => GitFileStates.FileName(Path);

    public string Directory => GitFileStates.Directory(Path);

    public string DisplayName => HasOldPath ? $"{GitFileStates.FileName(OldPath!)} → {FileName}" : FileName;

    public string StateLetter => GitFileStates.Letter(Change.State);

    public StatusTone StateTone => GitFileStates.Tone(Change.State);

    public string StateDescription => GitFileStates.Describe(Change.State);

    public int Additions => Change.Additions;

    public int Deletions => Change.Deletions;

    public bool IsBinary => Change.IsBinary;

    public double AdditionsBarWidth { get; }

    public double DeletionsBarWidth { get; }

    /// <summary>"+12 −3", or "binary".</summary>
    public string ChangesText => IsBinary ? "binary" : $"+{Additions} −{Deletions}";

    public string ToolTip => (HasOldPath ? $"{OldPath} → {Path}" : Path) + $"\n{StateDescription} · {ChangesText}";
}

/// <summary>A parent of a commit, shown as a link that selects it.</summary>
public sealed record ParentLink(string Sha)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}
