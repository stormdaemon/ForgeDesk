using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Files;

/// <summary>What the right pane of the Files tab shows.</summary>
public enum FileViewKind
{
    /// <summary>Nothing selected.</summary>
    None,
    Text,
    Image,
    Binary,
    Folder,
}

/// <summary>A clickable segment of the path breadcrumb ("src" › "app" › "main.cs").</summary>
public sealed record BreadcrumbSegment(string Name, string RelativePath, bool IsLast);

/// <summary>
/// The file preview of the Files tab: breadcrumb, header (size, encoding, line endings, lines,
/// modified time, git state), and the content as text (syntax highlighted), image or a binary
/// notice. Loads are cancellable and a newer load always wins over a stale one.
/// </summary>
public sealed partial class FileViewerViewModel : ViewModelBase, IDisposable
{
    /// <summary>Text beyond this size is truncated (and can be opened externally).</summary>
    public const long MaxTextBytes = 4 * 1024 * 1024;

    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly IFileService _files;
    private CancellationTokenSource? _load;
    private int _loadVersion;
    private FileDecorations _decorations = FileDecorations.Empty;

    public FileViewerViewModel(ProjectContext context, WorkspaceServices services, IFileService files)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        _context = context;
        _services = services;
        _files = files;
    }

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsImage), nameof(IsBinary), nameof(IsFolder), nameof(IsNone), nameof(HasFile), nameof(ShowContent))]
    public partial FileViewKind Kind { get; private set; }

    public bool IsText => Kind == FileViewKind.Text;

    public bool IsImage => Kind == FileViewKind.Image;

    public bool IsBinary => Kind == FileViewKind.Binary;

    public bool IsFolder => Kind == FileViewKind.Folder;

    public bool IsNone => Kind == FileViewKind.None && !IsLoading && Error is null;

    /// <summary>A file (not a folder) is shown: the header and file actions apply.</summary>
    public bool HasFile => Kind is FileViewKind.Text or FileViewKind.Image or FileViewKind.Binary;

    /// <summary>The content area is visible (not loading, no error).</summary>
    public bool ShowContent => !IsLoading && Error is null && Kind != FileViewKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNone), nameof(ShowContent))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Project-relative path of the shown file or folder ("" for the project root).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath), nameof(HasPath))]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand), nameof(OpenWithDefaultAppCommand), nameof(RevealCommand), nameof(CopyPathCommand), nameof(CopyRelativePathCommand))]
    public partial string? RelativePath { get; private set; }

    public bool HasPath => RelativePath is not null;

    public string? FullPath => RelativePath is null ? null : RelativePath.Length == 0 ? _context.Root : SafeResolve(RelativePath);

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>The text shown by the code view (null unless <see cref="IsText"/>).</summary>
    [ObservableProperty]
    public partial string? Text { get; private set; }

    /// <summary>1-based line to reveal in the code view.</summary>
    [ObservableProperty]
    public partial int? ScrollToLine { get; private set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    public partial string? TruncatedMessage { get; private set; }

    [ObservableProperty]
    public partial string? SizeText { get; private set; }

    [ObservableProperty]
    public partial string? EncodingText { get; private set; }

    [ObservableProperty]
    public partial string? LineEndingsText { get; private set; }

    [ObservableProperty]
    public partial string? LineCountText { get; private set; }

    [ObservableProperty]
    public partial string? ModifiedText { get; private set; }

    [ObservableProperty]
    public partial string? ModifiedToolTip { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitState), nameof(GitStateText), nameof(GitTone))]
    public partial FileGitState GitState { get; private set; }

    public bool HasGitState => GitState != FileGitState.None;

    public string GitStateText => FileDecorations.DescriptionOf(GitState);

    public StatusTone GitTone => FileDecorations.ToneOf(GitState);

    /// <summary>Full path of the image shown by the preview (null unless <see cref="IsImage"/>).</summary>
    [ObservableProperty]
    public partial string? ImagePath { get; private set; }

    /// <summary>Image preview: fit to the pane (true) or actual size (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial bool IsImageFit { get; set; } = true;

    public string ZoomText => IsImageFit ? "Fit" : "100%";

    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    /// <summary>Folder summary ("12 folders · 40 files").</summary>
    [ObservableProperty]
    public partial string? FolderSummary { get; private set; }

    /// <summary>The load in flight (tests wait on it).</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Shows a file, optionally revealing a line. Cancels any load in progress.</summary>
    public Task LoadAsync(string relativePath, int? line = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.Equals(RelativePath, relativePath, StringComparison.Ordinal) && HasFile && !IsLoading && Error is null)
        {
            // Same file: only move to the line (null first so the same line is revealed again).
            ScrollToLine = null;
            ScrollToLine = line;
            return Task.CompletedTask;
        }

        PendingLoad = LoadCoreAsync(relativePath, line);
        return PendingLoad;
    }

    /// <summary>Shows a folder summary (no I/O beyond the counts the tree already has).</summary>
    public void ShowFolder(string relativePath, string name, int? folders, int? files)
    {
        CancelPendingLoad();
        Error = null;
        IsLoading = false;
        SetPath(relativePath, name);
        ResetContent();
        GitState = FileGitState.None;
        FolderSummary = folders is null || files is null
            ? "Expand the folder to list its content."
            : folders == 0 && files == 0 ? "This folder is empty." : $"{Format.Count(folders.Value, "folder")} · {Format.Count(files.Value, "file")}";
        Kind = FileViewKind.Folder;
    }

    /// <summary>A file reported as deleted: explains it instead of failing to read it.</summary>
    public void ShowDeleted(string relativePath)
    {
        CancelPendingLoad();
        IsLoading = false;
        SetPath(relativePath, FileLocation.NameOf(relativePath));
        ResetContent();
        Kind = FileViewKind.None;
        GitState = FileGitState.Deleted;
        Error = new ErrorInfo(ErrorKind.PathNotFound, "File deleted", $"{Name} was deleted from the working tree.",
            "Use “Show changes in Git” to review the deletion or discard it to restore the file.");
    }

    /// <summary>Clears the pane (nothing selected).</summary>
    public void Clear()
    {
        CancelPendingLoad();
        Error = null;
        IsLoading = false;
        RelativePath = null;
        Name = string.Empty;
        Breadcrumbs.Clear();
        ResetContent();
        GitState = FileGitState.None;
        Kind = FileViewKind.None;
    }

    /// <summary>Refreshes the git pill after a status refresh.</summary>
    public void ApplyDecorations(FileDecorations decorations)
    {
        _decorations = decorations ?? FileDecorations.Empty;
        GitState = HasFile && RelativePath is { } path ? _decorations.StateOf(path) : FileGitState.None;
    }

    /// <summary>Reloads the shown file (after it changed on disk) without flashing the loading state.</summary>
    public Task ReloadAsync()
    {
        if (RelativePath is not { } path || !HasFile && Error is null)
        {
            return Task.CompletedTask;
        }

        PendingLoad = LoadCoreAsync(path, ScrollToLine, quiet: true);
        return PendingLoad;
    }

    [RelayCommand]
    private Task RetryAsync() => RelativePath is { } path ? LoadCoreAsync(path, ScrollToLine) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsLoading))]
    private void CancelLoad()
    {
        CancelPendingLoad();
        IsLoading = false;
        if (Kind == FileViewKind.None)
        {
            Error = new ErrorInfo(ErrorKind.Cancelled, "Loading cancelled", $"{Name} was not loaded.", "Select the file again or use Retry to load it.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void OpenInEditor()
    {
        if (FullPath is { } path)
        {
            Try(() => _services.Shell.OpenInEditor(path, IsText ? ScrollToLine : null), "Could not open the code editor");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void OpenWithDefaultApp()
    {
        if (FullPath is { } path)
        {
            Try(() => _services.Shell.OpenWithDefaultApp(path), "Could not open the file");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void Reveal()
    {
        if (FullPath is { } path)
        {
            Try(() =>
            {
                if (RelativePath is { Length: 0 })
                {
                    _services.Shell.OpenFolder(path);
                }
                else
                {
                    _services.Shell.RevealInExplorer(path);
                }
            }, "Could not open File Explorer");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void CopyPath()
    {
        if (FullPath is { } path)
        {
            Try(() =>
            {
                _services.Shell.CopyToClipboard(path);
                _services.Notifications.Show("Path copied", path);
            }, "Could not copy the path");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void CopyRelativePath()
    {
        if (RelativePath is { } path)
        {
            Try(() =>
            {
                _services.Shell.CopyToClipboard(path.Length == 0 ? "." : path);
                _services.Notifications.Show("Relative path copied", path.Length == 0 ? "." : path);
            }, "Could not copy the path");
        }
    }

    [RelayCommand]
    private void ToggleZoom() => IsImageFit = !IsImageFit;

    public void Dispose() => CancelPendingLoad();

    private async Task LoadCoreAsync(string relativePath, int? line, bool quiet = false)
    {
        CancelPendingLoad();
        var version = ++_loadVersion;
        CancellationTokenSource cts;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _load = cts;
        if (!quiet)
        {
            SetPath(relativePath, FileLocation.NameOf(relativePath));
            Error = null;
            IsLoading = true;
            Kind = FileViewKind.None;
            ResetContent();
            GitState = FileGitState.None;
        }

        try
        {
            var content = await _files.ReadAsync(_context.Root, relativePath, MaxTextBytes, cts.Token).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            Apply(content, line);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // A newer load replaced this one, or the user cancelled.
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                Kind = FileViewKind.None;
                ResetContent();
                Error = ErrorInfo.From(ex, ex is ForgeException { Kind: ErrorKind.PathNotFound } ? "File not found" : "Could not open this file");
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                if (ReferenceEquals(_load, cts))
                {
                    _load = null;
                }
            }

            cts.Dispose();
        }
    }

    private void Apply(FileContent content, int? line)
    {
        Error = null;
        SizeText = Format.Bytes(content.Size);
        ModifiedText = content.LastModified == default ? null : $"Modified {Format.RelativeTime(content.LastModified, _services.Time.GetLocalNow())}";
        ModifiedToolTip = content.LastModified == default ? null : Format.Timestamp(content.LastModified);
        GitState = _decorations.StateOf(content.RelativePath.Replace('\\', '/'));
        FolderSummary = null;
        switch (content.Kind)
        {
            case FileContentKind.Text:
                EncodingText = content.EncodingName;
                LineEndingsText = content.LineEndings;
                LineCountText = Format.Count(content.LineCount, "line");
                IsTruncated = content.IsTruncated;
                TruncatedMessage = content.IsTruncated
                    ? $"Showing the first {Format.Bytes(MaxTextBytes)} of {Format.Bytes(content.Size)}. Open the file in your editor to see all of it."
                    : null;
                ImagePath = null;
                ScrollToLine = null;
                Text = content.Text ?? string.Empty;
                ScrollToLine = line;
                Kind = FileViewKind.Text;
                break;
            case FileContentKind.Image:
                ClearTextInfo();
                Text = null;
                ImagePath = FullPath;
                Kind = FileViewKind.Image;
                break;
            default:
                ClearTextInfo();
                Text = null;
                ImagePath = null;
                Kind = FileViewKind.Binary;
                break;
        }
    }

    private void SetPath(string relativePath, string name)
    {
        RelativePath = relativePath;
        Name = relativePath.Length == 0 ? _context.Project.Name : name;
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbSegment(_context.Project.Name, string.Empty, relativePath.Length == 0));
        if (relativePath.Length == 0)
        {
            return;
        }

        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            Breadcrumbs.Add(new BreadcrumbSegment(segments[i], string.Join('/', segments, 0, i + 1), i == segments.Length - 1));
        }
    }

    private void ResetContent()
    {
        Text = null;
        ImagePath = null;
        ScrollToLine = null;
        FolderSummary = null;
        SizeText = null;
        ModifiedText = null;
        ModifiedToolTip = null;
        ClearTextInfo();
    }

    private void ClearTextInfo()
    {
        EncodingText = null;
        LineEndingsText = null;
        LineCountText = null;
        IsTruncated = false;
        TruncatedMessage = null;
    }

    private void CancelPendingLoad()
    {
        _loadVersion++;
        var load = _load;
        _load = null;
        try
        {
            load?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string? SafeResolve(string relativePath)
    {
        try
        {
            return PathUtil.ResolveUnder(_context.Root, relativePath);
        }
        catch (Exception ex) when (ex is ForgeException or ArgumentException)
        {
            return null;
        }
    }

    private void Try(Action action, string errorTitle)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, errorTitle));
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName == nameof(Error))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNone)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowContent)));
        }
    }
}
