using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Files;

/// <summary>
/// A file or folder row of the Files tab. Tree rows are flattened (only visible nodes are in the
/// list, indented by <see cref="Depth"/>) so huge folders stay fluid; the same type is used for
/// the flat lists ("Only changed files", "Go to file" results) with <see cref="IsFlat"/> set.
/// Node instances survive refreshes (merged by path) so expansion and selection are kept.
/// </summary>
public sealed partial class FileTreeNodeViewModel : ObservableObject
{
    public const double IndentPerLevel = 16;

    public FileTreeNodeViewModel(FileEntry entry, int depth, bool isFlat = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RelativePath = entry.RelativePath.Replace('\\', '/');
        Depth = depth;
        IsFlat = isFlat;
        IsDirectory = entry.IsDirectory;
        Update(entry);
    }

    /// <summary>A row for a path that does not exist on disk (a deleted file in "Only changed files").</summary>
    public static FileTreeNodeViewModel ForMissingFile(string relativePath) =>
        new(new FileEntry { Name = FileLocation.NameOf(relativePath), RelativePath = relativePath }, 0, isFlat: true) { Exists = false };

    public static FileTreeNodeViewModel ForFlatFile(string relativePath) =>
        new(new FileEntry { Name = FileLocation.NameOf(relativePath), RelativePath = relativePath }, 0, isFlat: true);

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public int Depth { get; }

    /// <summary>Row of a flat list: shows the folder next to the name instead of indenting.</summary>
    public bool IsFlat { get; }

    public double Indent => IsFlat ? 0 : Depth * IndentPerLevel;

    /// <summary>Folders of the tree show an expand/collapse chevron.</summary>
    public bool HasChevron => IsDirectory && !IsFlat;

    /// <summary>Parent folder ("src/app"), shown dimmed in flat lists.</summary>
    public string Directory => FileLocation.ParentOf(RelativePath);

    public bool HasDirectory => IsFlat && Directory.Length > 0;

    /// <summary>False for deleted files listed by "Only changed files".</summary>
    public bool Exists { get; private init; } = true;

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial long Size { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset LastModified { get; private set; }

    [ObservableProperty]
    public partial bool IsHidden { get; private set; }

    /// <summary>node_modules, bin, obj…: dimmed and never expanded automatically.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed))]
    public partial bool IsHeavy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed))]
    public partial bool IsIgnored { get; private set; }

    public bool IsDimmed => IsHeavy || IsIgnored || !Exists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Why the folder could not be listed (shown as the folder's tooltip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ToolTip))]
    public partial string? LoadError { get; set; }

    public bool HasLoadError => LoadError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitLetter), nameof(GitTone), nameof(HasGitState), nameof(IsConflicted), nameof(ToolTip))]
    public partial FileGitState GitState { get; set; }

    /// <summary>Folders: something inside changed (a dot is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitTone), nameof(ToolTip))]
    public partial FileGitState FolderGitState { get; set; }

    public bool ContainsChanges => FolderGitState != FileGitState.None;

    public bool HasGitState => GitState != FileGitState.None;

    public bool IsConflicted => GitState == FileGitState.Conflicted;

    public string GitLetter => FileDecorations.LetterOf(GitState);

    /// <summary>Color of the name, letter and folder dot.</summary>
    public StatusTone GitTone => IsDirectory
        ? FolderGitState == FileGitState.Conflicted ? StatusTone.Danger : FolderGitState != FileGitState.None ? StatusTone.Warning : StatusTone.None
        : FileDecorations.ToneOf(GitState);

    /// <summary>WPF-UI symbol name of the row icon.</summary>
    public string Icon => IsDirectory ? (IsExpanded ? "FolderOpen16" : "Folder16") : FileIcons.For(Name);

    public string ToolTip
    {
        get
        {
            if (LoadError is { } error)
            {
                return error;
            }

            var parts = new List<string> { RelativePath.Length == 0 ? Name : RelativePath };
            if (IsHeavy)
            {
                parts.Add("Generated or dependency folder");
            }
            else if (IsIgnored)
            {
                parts.Add("Ignored by .gitignore");
            }

            if (GitState != FileGitState.None)
            {
                parts.Add(FileDecorations.DescriptionOf(GitState));
            }
            else if (IsDirectory && FolderGitState == FileGitState.Conflicted)
            {
                parts.Add("Contains conflicted files");
            }
            else if (IsDirectory && FolderGitState != FileGitState.None)
            {
                parts.Add("Contains changes");
            }

            return string.Join(" · ", parts);
        }
    }

    // ----- Tree state (owned by FilesSectionViewModel) ---------------------------------

    /// <summary>Loaded children (null until the folder was listed).</summary>
    internal List<FileTreeNodeViewModel>? Children { get; set; }

    internal bool IsLoaded => Children is not null;

    /// <summary>Loaded, but files changed since: re-listed when expanded again.</summary>
    internal bool IsStale { get; set; }

    /// <summary>Refreshes the entry data (size, flags) after a re-listing; keeps tree state.</summary>
    internal void Update(FileEntry entry)
    {
        Name = entry.Name;
        Size = entry.Size;
        LastModified = entry.LastModified;
        IsHidden = entry.IsHidden;
        IsHeavy = entry.IsHeavyFolder;
        IsIgnored = entry.IsIgnored;
    }

    internal void ApplyDecorations(FileDecorations decorations)
    {
        if (IsDirectory)
        {
            FolderGitState = decorations.FolderStateOf(RelativePath);
            OnPropertyChanged(nameof(ContainsChanges));
        }
        else
        {
            GitState = decorations.StateOf(RelativePath);
        }
    }

    public override string ToString() => RelativePath;
}

/// <summary>Row icons by file type (WPF-UI SymbolRegular names).</summary>
public static class FileIcons
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".svg",
    };

    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rs", ".go", ".java", ".kt", ".kts",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".swift", ".rb", ".php", ".lua", ".dart", ".scala", ".sh", ".bash", ".zsh",
        ".ps1", ".psm1", ".bat", ".cmd", ".sql", ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte", ".xaml", ".razor",
        ".cshtml",
    };

    private static readonly HashSet<string> Data = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".yml", ".yaml", ".toml", ".xml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".slnx", ".sln",
        ".config", ".ini", ".env", ".lock", ".editorconfig", ".gitignore", ".gitattributes",
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc", ".log", ".csv",
    };

    public static string For(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrEmpty(extension) && fileName is { Length: > 1 } && fileName[0] == '.')
        {
            extension = fileName;
        }

        if (Images.Contains(extension))
        {
            return "Image16";
        }

        if (Code.Contains(extension))
        {
            return "Code16";
        }

        if (Data.Contains(extension))
        {
            return "DocumentBulletList16";
        }

        return Text.Contains(extension) ? "DocumentText16" : "Document16";
    }
}
