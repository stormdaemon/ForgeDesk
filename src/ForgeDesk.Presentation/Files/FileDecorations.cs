using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Files;

/// <summary>Git state of a file as shown by the tree and the file header.</summary>
public enum FileGitState
{
    None,
    Modified,
    Renamed,
    Added,
    Untracked,
    Deleted,
    Conflicted,
}

/// <summary>
/// Git decorations of the file tree, computed once per status refresh: the state of every
/// changed file and, for each folder, whether it contains changes (and conflicts).
/// </summary>
public sealed class FileDecorations
{
    public static readonly FileDecorations Empty = new(new Dictionary<string, FileGitState>(PathUtil.Comparer), new Dictionary<string, FileGitState>(PathUtil.Comparer), []);

    private readonly Dictionary<string, FileGitState> _files;
    private readonly Dictionary<string, FileGitState> _folders;

    private FileDecorations(Dictionary<string, FileGitState> files, Dictionary<string, FileGitState> folders, IReadOnlyList<GitStatusEntry> entries)
    {
        _files = files;
        _folders = folders;
        Entries = entries;
    }

    /// <summary>The changed paths, sorted (drives the "Only changed files" list).</summary>
    public IReadOnlyList<GitStatusEntry> Entries { get; }

    public int Count => _files.Count;

    public static FileDecorations From(GitStatus? status)
    {
        if (status is null || status.Entries.Count == 0)
        {
            return Empty;
        }

        var files = new Dictionary<string, FileGitState>(PathUtil.Comparer);
        var folders = new Dictionary<string, FileGitState>(PathUtil.Comparer);
        foreach (var entry in status.Entries)
        {
            var path = FileLocation.Normalize(entry.Path);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var state = StateOf(entry);
            files[path] = state;
            var folderState = state == FileGitState.Conflicted ? FileGitState.Conflicted : FileGitState.Modified;
            foreach (var folder in FileLocation.AncestorsOf(path))
            {
                if (!folders.TryGetValue(folder, out var existing) || existing != FileGitState.Conflicted)
                {
                    folders[folder] = folderState;
                }
            }
        }

        var sorted = status.Entries
            .Where(e => !string.IsNullOrEmpty(FileLocation.Normalize(e.Path)))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FileDecorations(files, folders, sorted);
    }

    /// <summary>State of a file, or <see cref="FileGitState.None"/> when it has no changes.</summary>
    public FileGitState StateOf(string relativePath) =>
        _files.TryGetValue(relativePath, out var state) ? state : FileGitState.None;

    /// <summary>
    /// For a folder: <see cref="FileGitState.Conflicted"/> when a file inside is conflicted,
    /// <see cref="FileGitState.Modified"/> when something inside changed, otherwise None.
    /// </summary>
    public FileGitState FolderStateOf(string relativePath) =>
        _folders.TryGetValue(relativePath, out var state) ? state : FileGitState.None;

    /// <summary>Maps a status entry to the single state shown for it (most important first).</summary>
    public static FileGitState StateOf(GitStatusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsConflicted)
        {
            return FileGitState.Conflicted;
        }

        if (entry.IsUntracked)
        {
            return FileGitState.Untracked;
        }

        if (entry.WorkTreeState == GitFileState.Deleted || entry.IndexState == GitFileState.Deleted)
        {
            return FileGitState.Deleted;
        }

        if (entry.IndexState == GitFileState.Added)
        {
            return FileGitState.Added;
        }

        if (entry.IndexState is GitFileState.Renamed or GitFileState.Copied)
        {
            return FileGitState.Renamed;
        }

        return FileGitState.Modified;
    }

    public static StatusTone ToneOf(FileGitState state) => state switch
    {
        FileGitState.Modified or FileGitState.Renamed => StatusTone.Warning,
        FileGitState.Added or FileGitState.Untracked => StatusTone.Success,
        FileGitState.Deleted or FileGitState.Conflicted => StatusTone.Danger,
        _ => StatusTone.None,
    };

    /// <summary>One-letter badge (M, R, A, U, D, !).</summary>
    public static string LetterOf(FileGitState state) => state switch
    {
        FileGitState.Modified => "M",
        FileGitState.Renamed => "R",
        FileGitState.Added => "A",
        FileGitState.Untracked => "U",
        FileGitState.Deleted => "D",
        FileGitState.Conflicted => "!",
        _ => string.Empty,
    };

    public static string DescriptionOf(FileGitState state) => state switch
    {
        FileGitState.Modified => "Modified",
        FileGitState.Renamed => "Renamed",
        FileGitState.Added => "Added",
        FileGitState.Untracked => "Untracked",
        FileGitState.Deleted => "Deleted",
        FileGitState.Conflicted => "Conflicted",
        _ => "Unchanged",
    };
}
