using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>How a file state is shown: a one-letter glyph, a color and a word.</summary>
public static class GitFileStates
{
    /// <summary>"M", "A", "D", "R", "C", "T", "?" (untracked), "U" (conflicted).</summary>
    public static string Letter(GitFileState state) => state switch
    {
        GitFileState.Modified => "M",
        GitFileState.Added => "A",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Copied => "C",
        GitFileState.TypeChanged => "T",
        GitFileState.Untracked => "?",
        GitFileState.Conflicted => "U",
        GitFileState.Ignored => "!",
        _ => " ",
    };

    /// <summary>Amber for modifications, green for new files, red for deletions and conflicts, blue for renames.</summary>
    public static StatusTone Tone(GitFileState state) => state switch
    {
        GitFileState.Modified => StatusTone.Warning,
        GitFileState.Added or GitFileState.Untracked => StatusTone.Success,
        GitFileState.Deleted or GitFileState.Conflicted => StatusTone.Danger,
        GitFileState.Renamed or GitFileState.Copied or GitFileState.TypeChanged => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    public static string Describe(GitFileState state) => state switch
    {
        GitFileState.Modified => "Modified",
        GitFileState.Added => "Added",
        GitFileState.Deleted => "Deleted",
        GitFileState.Renamed => "Renamed",
        GitFileState.Copied => "Copied",
        GitFileState.TypeChanged => "Type changed",
        GitFileState.Untracked => "New file",
        GitFileState.Conflicted => "Conflicted",
        GitFileState.Ignored => "Ignored",
        _ => "Unchanged",
    };

    /// <summary>"app.cs" from "src/app.cs".</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>"src/" from "src/app.cs"; empty at the repository root.</summary>
    public static string Directory(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..(slash + 1)];
    }
}
