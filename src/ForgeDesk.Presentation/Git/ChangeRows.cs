using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>The groups of the Changes list, in display order.</summary>
public enum ChangeGroup
{
    Conflicted,
    Staged,
    Unstaged,
}

/// <summary>A row of the Changes list: a group header or a file.</summary>
public abstract class ChangeListRow : ObservableObject
{
    public abstract bool IsHeader { get; }
}

/// <summary>"Merge conflicts (2)", "Staged changes (3)", "Changes (5)".</summary>
public sealed partial class ChangeGroupHeader : ChangeListRow
{
    public ChangeGroupHeader(ChangeGroup group)
    {
        Group = group;
        Title = group switch
        {
            ChangeGroup.Conflicted => "Merge conflicts",
            ChangeGroup.Staged => "Staged changes",
            _ => "Changes",
        };
    }

    public ChangeGroup Group { get; }

    public string Title { get; }

    public override bool IsHeader => true;

    public bool IsConflictGroup => Group == ChangeGroup.Conflicted;

    [ObservableProperty]
    public partial int Count { get; internal set; }
}

/// <summary>A changed file in one group. A partially staged file has one row in each group.</summary>
public sealed partial class ChangeItemViewModel : ChangeListRow
{
    public ChangeItemViewModel(ChangeGroup group, GitStatusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Group = group;
        Entry = entry;
    }

    public ChangeGroup Group { get; }

    public override bool IsHeader => false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Path), nameof(OldPath), nameof(HasOldPath), nameof(FileName), nameof(Directory), nameof(DisplayName),
        nameof(State), nameof(StateLetter), nameof(StateTone), nameof(StateDescription), nameof(IsUntracked), nameof(ToolTip))]
    public partial GitStatusEntry Entry { get; private set; }

    public string Path => Entry.Path;

    /// <summary>Previous path of a staged rename or copy.</summary>
    public string? OldPath => Group == ChangeGroup.Staged ? Entry.OriginalPath : null;

    public bool HasOldPath => OldPath is not null && !string.Equals(OldPath, Path, StringComparison.Ordinal);

    public string FileName => GitFileStates.FileName(Path);

    /// <summary>Folder of the file with a trailing slash ("src/app/"), empty at the root and for moves (shown in full).</summary>
    public string Directory => IsMove ? string.Empty : GitFileStates.Directory(Path);

    /// <summary>"app.cs"; "old.cs → app.cs" for a rename; "old/app.cs → src/app.cs" for a move to another folder.</summary>
    public string DisplayName => !HasOldPath ? FileName
        : IsMove ? $"{OldPath} → {Path}"
        : $"{GitFileStates.FileName(OldPath!)} → {FileName}";

    /// <summary>Same file name, other folder.</summary>
    private bool IsMove => HasOldPath && string.Equals(GitFileStates.FileName(OldPath!), FileName, StringComparison.Ordinal);

    public GitFileState State => Group switch
    {
        ChangeGroup.Conflicted => GitFileState.Conflicted,
        ChangeGroup.Staged => Entry.IndexState,
        _ => Entry.WorkTreeState,
    };

    public string StateLetter => GitFileStates.Letter(State);

    public StatusTone StateTone => GitFileStates.Tone(State);

    public string StateDescription => GitFileStates.Describe(State);

    public bool IsStaged => Group == ChangeGroup.Staged;

    /// <summary>Not staged and not conflicted: offers "Stage" (conflicted files offer "Mark as resolved").</summary>
    public bool CanStage => Group == ChangeGroup.Unstaged;

    /// <summary>Staging a conflicted file marks it resolved.</summary>
    public string StageText => IsConflicted ? "Mark as resolved" : "Stage";

    public bool IsConflicted => Group == ChangeGroup.Conflicted;

    public bool IsUntracked => Group == ChangeGroup.Unstaged && Entry.IsUntracked;

    /// <summary>A deleted file cannot be opened.</summary>
    public bool Exists => State != GitFileState.Deleted;

    public string ToolTip
    {
        get
        {
            var where = Group switch
            {
                ChangeGroup.Staged => "staged",
                ChangeGroup.Conflicted => "unresolved conflict",
                _ => "not staged",
            };
            var path = HasOldPath ? $"{OldPath} → {Path}" : Path;
            return $"{path}\n{StateDescription} ({where})";
        }
    }

    internal (ChangeGroup Group, string Path) Key => (Group, Path);

    internal void Update(GitStatusEntry entry)
    {
        if (entry != Entry)
        {
            Entry = entry;
        }
    }
}
