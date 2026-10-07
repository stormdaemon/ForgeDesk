using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Files;

/// <summary>
/// The Files tab: a lazy, flattened and virtualized file tree with git decorations ("Only changed
/// files" and "Go to file" flat modes), a file preview, and "Search in files". Built to stay fluid
/// on repositories with 100 000 files: folders are listed only when expanded, rows are the visible
/// nodes only, and refreshes after file-system changes re-list just the loaded folders.
/// </summary>
public sealed partial class FilesSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    /// <summary>Most "Go to file" results shown.</summary>
    public const int MaxGoToResults = 200;

    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly IFileService _files;
    private readonly IFileIndex _index;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly Dictionary<string, FileTreeNodeViewModel> _changedNodes = new(PathUtil.Comparer);
    private List<FileTreeNodeViewModel> _roots = [];
    private List<FileTreeNodeViewModel> _changedRows = [];
    private List<FileTreeNodeViewModel>? _goToRows;
    private FileDecorations _decorations;
    private Debouncer? _changeDebouncer;
    private CancellationTokenSource? _goTo;
    private bool _isActive;
    private bool _treeLoaded;
    private bool _treeStale;
    private bool _decorationsStale;
    private bool _quietSelection;
    private bool _disposed;

    public FilesSectionViewModel(ProjectContext context, WorkspaceServices services, IFileService files, IFileIndex index, IContentSearchService search)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        _context = context;
        _services = services;
        _files = files;
        _index = index;
        _decorations = FileDecorations.From(context.GitStatus);
        Viewer = new FileViewerViewModel(context, services, files);
        Viewer.ApplyDecorations(_decorations);
        Search = new FileSearchViewModel(context, search, services.Dispatcher, OpenFromSearchAsync);
        RebuildChangedRows();

        context.FilesChanged += OnFilesChanged;
        context.GitStatusChanged += OnGitStatusChanged;
    }

    public WorkspaceSection Section => WorkspaceSection.Files;

    public FileViewerViewModel Viewer { get; }

    public FileSearchViewModel Search { get; }

    /// <summary>Visible rows of the left pane (tree, changed files or "Go to file" results).</summary>
    public RowCollection<FileTreeNodeViewModel> Rows { get; } = new();

    /// <summary>Quiet period after file-system changes before the tree re-lists its folders.</summary>
    public TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Raised when the view should scroll a row into view (after navigation).</summary>
    public event EventHandler<FileTreeNodeViewModel>? RevealRequested;

    /// <summary>Raised when the view should move the keyboard focus to the "Go to file" box.</summary>
    public event EventHandler? FocusGoToRequested;

    public string ProjectName => _context.Project.Name;

    public bool IsGitRepository => _context.IsGitRepository;

    [ObservableProperty]
    public partial FileTreeNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial bool ShowHiddenFiles { get; set; }

    /// <summary>Flat list of the files git reports as changed instead of the tree.</summary>
    [ObservableProperty]
    public partial bool OnlyChangedFiles { get; set; }

    /// <summary>"Go to file" filter typed above the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoToActive))]
    [NotifyCanExecuteChangedFor(nameof(ClearGoToCommand))]
    public partial string GoToText { get; set; } = string.Empty;

    public bool IsGoToActive => !string.IsNullOrWhiteSpace(GoToText);

    [ObservableProperty]
    public partial bool IsGoToSearching { get; private set; }

    /// <summary>The search panel replaces the tree in the left pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeVisible))]
    public partial bool IsSearchOpen { get; set; }

    public bool IsTreeVisible => !IsSearchOpen;

    /// <summary>First listing of the project root in progress (skeleton rows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    public partial bool IsLoadingTree { get; private set; }

    public bool ShowRows => !IsLoadingTree && Error is null && !ShowEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    public partial bool ShowEmpty { get; private set; }

    [ObservableProperty]
    public partial string EmptyIcon { get; private set; } = "FolderOpen24";

    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? EmptyDescription { get; private set; }

    [ObservableProperty]
    public partial string? EmptyActionText { get; private set; }

    [ObservableProperty]
    public partial IRelayCommand? EmptyActionCommand { get; private set; }

    /// <summary>"12 changed files" (tooltip of the "Only changed files" toggle).</summary>
    [ObservableProperty]
    public partial string ChangedFilesText { get; private set; } = "No changed files";

    [ObservableProperty]
    public partial int ChangedCount { get; private set; }

    /// <summary>Error of the "Go to file" index (shown in the empty state).</summary>
    [ObservableProperty]
    public partial string? GoToError { get; private set; }

    // ----- Lifecycle --------------------------------------------------------------------

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        _isActive = true;
        if (_decorationsStale)
        {
            ApplyDecorations();
        }

        if (!_treeLoaded)
        {
            await LoadTreeAsync().ConfigureAwait(true);
        }
        else if (_treeStale)
        {
            _treeStale = false;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    public void Deactivate() => _isActive = false;

    public async Task NavigateToAsync(object argument)
    {
        if (FileLocation.From(argument) is { } location)
        {
            await RevealAsync(location).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isActive = false;
        _context.FilesChanged -= OnFilesChanged;
        _context.GitStatusChanged -= OnGitStatusChanged;
        _changeDebouncer?.Dispose();
        CancelGoTo();
        Viewer.Dispose();
        Search.Dispose();
    }

    // ----- Loading ----------------------------------------------------------------------

    /// <summary>F5: re-lists the loaded folders, re-reads the shown file and git status.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _index.Invalidate(_context.Root);
        if (!_treeLoaded)
        {
            await LoadTreeAsync().ConfigureAwait(true);
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
        await Viewer.ReloadAsync().ConfigureAwait(true);
        try
        {
            await _context.RefreshGitStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
    }

    private async Task LoadTreeAsync()
    {
        IsLoadingTree = !_treeLoaded;
        try
        {
            await RunAsync(async () =>
            {
                var entries = await _files.ListDirectoryAsync(_context.Root, string.Empty, _context.Lifetime).ConfigureAwait(true);
                _roots = FileTree.Merge(_roots, entries, 0) ?? _roots;
                _treeLoaded = true;
                foreach (var node in FileTree.AllLoaded(_roots))
                {
                    node.ApplyDecorations(_decorations);
                }

                UpdateRows();
            }, errorTitle: "Could not list the project files").ConfigureAwait(true);
        }
        finally
        {
            IsLoadingTree = false;
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// Re-lists the root and every expanded folder, merging into the existing nodes. Collapsed
    /// folders that were loaded are marked stale and re-listed when expanded again.
    /// </summary>
    private async Task ReloadAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _reloadGate.WaitAsync(_context.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation() || ex is ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_treeLoaded)
            {
                await LoadTreeAsync().ConfigureAwait(true);
                return;
            }

            IReadOnlyList<FileEntry> rootEntries;
            try
            {
                rootEntries = await _files.ListDirectoryAsync(_context.Root, string.Empty, _context.Lifetime).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex.IsCancellation())
            {
                return;
            }
            catch (Exception ex)
            {
                Error = ErrorInfo.From(ex, "Could not list the project files");
                UpdateEmptyState();
                return;
            }

            Error = null;
            var merged = FileTree.Merge(_roots, rootEntries, 0);
            if (merged is not null)
            {
                _roots = merged;
            }

            foreach (var folder in FileTree.LoadedFolders(_roots).ToList())
            {
                if (folder.IsExpanded && IsReachable(folder))
                {
                    await LoadChildrenAsync(folder).ConfigureAwait(true);
                }
                else
                {
                    folder.IsStale = true;
                }
            }

            foreach (var node in FileTree.AllLoaded(_roots))
            {
                node.ApplyDecorations(_decorations);
            }

            UpdateRows();
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>Lists a folder's content into its node (errors are shown on the folder row).</summary>
    private async Task LoadChildrenAsync(FileTreeNodeViewModel folder)
    {
        folder.IsLoading = true;
        try
        {
            var entries = await _files.ListDirectoryAsync(_context.Root, folder.RelativePath, _context.Lifetime).ConfigureAwait(true);
            var merged = FileTree.Merge(folder.Children, entries, folder.Depth + 1);
            if (merged is not null)
            {
                folder.Children = merged;
            }

            folder.Children ??= [];
            folder.LoadError = null;
            folder.IsStale = false;
            foreach (var child in folder.Children)
            {
                child.ApplyDecorations(_decorations);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            folder.LoadError = ErrorInfo.From(ex).Message;
            folder.Children ??= [];
        }
        finally
        {
            folder.IsLoading = false;
        }
    }

    // ----- Tree interaction -------------------------------------------------------------

    /// <summary>Expands or collapses a folder (chevron click, Enter, double-click).</summary>
    [RelayCommand]
    private async Task ToggleNodeAsync(FileTreeNodeViewModel? node)
    {
        if (node is not { IsDirectory: true, IsFlat: false })
        {
            return;
        }

        if (node.IsExpanded)
        {
            Collapse(node);
        }
        else
        {
            await ExpandAsync(node).ConfigureAwait(true);
        }
    }

    /// <summary>Right arrow: expands a folder.</summary>
    [RelayCommand]
    private Task ExpandNodeAsync(FileTreeNodeViewModel? node) =>
        node is { IsDirectory: true, IsFlat: false, IsExpanded: false } ? ExpandAsync(node) : Task.CompletedTask;

    /// <summary>Left arrow: collapses a folder, or selects the parent folder.</summary>
    [RelayCommand]
    private void CollapseNode(FileTreeNodeViewModel? node)
    {
        if (node is null || node.IsFlat)
        {
            return;
        }

        if (node.IsDirectory && node.IsExpanded)
        {
            Collapse(node);
            return;
        }

        var parentPath = FileLocation.ParentOf(node.RelativePath);
        if (parentPath.Length > 0 && Rows.FirstOrDefault(r => string.Equals(r.RelativePath, parentPath, StringComparison.Ordinal)) is { } parent)
        {
            SelectedNode = parent;
            RevealRequested?.Invoke(this, parent);
        }
    }

    /// <summary>Enter / double-click: folders toggle, files open in the code editor.</summary>
    [RelayCommand]
    private Task ActivateNodeAsync(FileTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return Task.CompletedTask;
        }

        if (node.IsFlat && IsGoToActive)
        {
            return RevealAsync(new FileLocation(node.RelativePath));
        }

        if (node.IsDirectory)
        {
            return ToggleNodeAsync(node);
        }

        OpenInEditor(node);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var folder in FileTree.LoadedFolders(_roots))
        {
            folder.IsExpanded = false;
        }

        if (SelectedNode is { IsFlat: false, Depth: > 0 })
        {
            SelectedNode = null;
        }

        UpdateRows();
    }

    private async Task ExpandAsync(FileTreeNodeViewModel node)
    {
        node.IsExpanded = true;
        if (!node.IsLoaded)
        {
            UpdateRows();
            await LoadChildrenAsync(node).ConfigureAwait(true);
        }
        else if (node.IsStale)
        {
            UpdateRows();
            await LoadChildrenAsync(node).ConfigureAwait(true);
        }

        if (node.IsExpanded)
        {
            UpdateRows();
        }
    }

    private void Collapse(FileTreeNodeViewModel node)
    {
        node.IsExpanded = false;
        if (SelectedNode is { } selected && selected.RelativePath.StartsWith(node.RelativePath + "/", StringComparison.Ordinal) && !selected.IsFlat)
        {
            SelectedNode = node;
        }

        UpdateRows();
    }

    partial void OnSelectedNodeChanged(FileTreeNodeViewModel? value)
    {
        if (_quietSelection || value is null || _disposed)
        {
            return;
        }

        ShowInViewer(value, null);
    }

    partial void OnShowHiddenFilesChanged(bool value) => UpdateRows();

    partial void OnOnlyChangedFilesChanged(bool value)
    {
        if (value)
        {
            RebuildChangedRows();
        }

        UpdateRows();
    }

    private void ShowInViewer(FileTreeNodeViewModel node, int? line)
    {
        if (!node.Exists)
        {
            Viewer.ShowDeleted(node.RelativePath);
        }
        else if (node.IsDirectory)
        {
            var children = node.Children;
            Viewer.ShowFolder(node.RelativePath, node.Name, children?.Count(c => c.IsDirectory), children?.Count(c => !c.IsDirectory));
        }
        else
        {
            _ = Viewer.LoadAsync(node.RelativePath, line);
        }
    }

    // ----- Rows -------------------------------------------------------------------------

    /// <summary>Recomputes the visible rows for the current mode, keeping the selection.</summary>
    private void UpdateRows()
    {
        if (_disposed)
        {
            return;
        }

        List<FileTreeNodeViewModel> desired;
        if (_goToRows is not null && IsGoToActive)
        {
            desired = _goToRows;
        }
        else if (OnlyChangedFiles)
        {
            desired = _changedRows.Where(n => ShowHiddenFiles || !IsHiddenPath(n.RelativePath)).ToList();
        }
        else
        {
            desired = FileTree.Flatten(_roots, n => ShowHiddenFiles || !n.IsHidden);
        }

        var selected = SelectedNode;
        _quietSelection = true;
        try
        {
            Rows.Update(desired);
            if (selected is not null && !ReferenceEquals(SelectedNode, selected))
            {
                SelectedNode = desired.Contains(selected)
                    ? selected
                    : desired.FirstOrDefault(n => string.Equals(n.RelativePath, selected.RelativePath, StringComparison.Ordinal));
            }
            else if (selected is not null && !desired.Contains(selected))
            {
                SelectedNode = desired.FirstOrDefault(n => string.Equals(n.RelativePath, selected.RelativePath, StringComparison.Ordinal));
            }
        }
        finally
        {
            _quietSelection = false;
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (IsLoadingTree || Error is not null || Rows.Count > 0)
        {
            ShowEmpty = false;
            return;
        }

        if (IsGoToActive)
        {
            if (IsGoToSearching || _goToRows is null)
            {
                ShowEmpty = false;
                return;
            }

            SetEmpty("DocumentSearch24", GoToError is null ? "No matching files" : "Go to file is unavailable",
                GoToError ?? $"No file name in {ProjectName} matches “{GoToText.Trim()}”.", "Clear filter", ClearGoToCommand);
        }
        else if (OnlyChangedFiles)
        {
            if (!_context.IsGitRepository)
            {
                SetEmpty("BranchFork24", "Not a Git repository", "Changed files are tracked by Git. This project is not under version control.",
                    "Show all files", ShowAllFilesCommand);
            }
            else
            {
                SetEmpty("CheckmarkCircle24", "No changes", "The working tree is clean: nothing was modified, added or deleted.",
                    "Show all files", ShowAllFilesCommand);
            }
        }
        else if (_treeLoaded)
        {
            SetEmpty("FolderOpen24", "This folder is empty",
                ShowHiddenFiles ? "There are no files in the project folder yet." : "There are no visible files here. Hidden files are not shown.",
                ShowHiddenFiles ? "Open in File Explorer" : "Show hidden files",
                ShowHiddenFiles ? OpenProjectFolderCommand : ShowHiddenCommand);
        }
        else
        {
            ShowEmpty = false;
        }
    }

    private void SetEmpty(string icon, string title, string? description, string? actionText, IRelayCommand? action)
    {
        EmptyIcon = icon;
        EmptyTitle = title;
        EmptyDescription = description;
        EmptyActionText = actionText;
        EmptyActionCommand = action;
        ShowEmpty = true;
    }

    [RelayCommand]
    private void ShowAllFiles()
    {
        OnlyChangedFiles = false;
        GoToText = string.Empty;
    }

    [RelayCommand]
    private void ShowHidden() => ShowHiddenFiles = true;

    [RelayCommand]
    private void OpenProjectFolder() => Try(() => _services.Shell.OpenFolder(_context.Root), "Could not open File Explorer");

    private static bool IsHiddenPath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.StartsWith('.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A folder is reachable when all its ancestors are expanded (so it is part of the tree on screen).</summary>
    private bool IsReachable(FileTreeNodeViewModel folder)
    {
        var level = _roots;
        foreach (var ancestor in FileLocation.AncestorsOf(folder.RelativePath))
        {
            var node = level.FirstOrDefault(n => string.Equals(n.RelativePath, ancestor, StringComparison.Ordinal));
            if (node is not { IsExpanded: true, Children: { } children })
            {
                return false;
            }

            level = children;
        }

        return true;
    }

    // ----- Git decorations --------------------------------------------------------------

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_isActive)
        {
            _decorationsStale = true;
            return;
        }

        ApplyDecorations();
    }

    private void ApplyDecorations()
    {
        _decorationsStale = false;
        _decorations = FileDecorations.From(_context.GitStatus);
        foreach (var node in FileTree.AllLoaded(_roots))
        {
            node.ApplyDecorations(_decorations);
        }

        if (_goToRows is not null)
        {
            foreach (var node in _goToRows)
            {
                node.GitState = _decorations.StateOf(node.RelativePath);
            }
        }

        Viewer.ApplyDecorations(_decorations);
        RebuildChangedRows();
        OnPropertyChanged(nameof(IsGitRepository));
        if (OnlyChangedFiles)
        {
            UpdateRows();
        }
    }

    private void RebuildChangedRows()
    {
        var rows = new List<FileTreeNodeViewModel>(_decorations.Entries.Count);
        var seen = new HashSet<string>(PathUtil.Comparer);
        foreach (var entry in _decorations.Entries)
        {
            var path = FileLocation.Normalize(entry.Path)!;
            if (!seen.Add(path))
            {
                continue;
            }

            var state = _decorations.StateOf(path);
            if (!_changedNodes.TryGetValue(path, out var node) || node.Exists != (state != FileGitState.Deleted))
            {
                node = state == FileGitState.Deleted ? FileTreeNodeViewModel.ForMissingFile(path) : FileTreeNodeViewModel.ForFlatFile(path);
                _changedNodes[path] = node;
            }

            node.GitState = state;
            rows.Add(node);
        }

        foreach (var stale in _changedNodes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _changedNodes.Remove(stale);
        }

        _changedRows = rows;
        ChangedCount = rows.Count;
        ChangedFilesText = rows.Count == 0 ? "No changed files" : Format.Count(rows.Count, "changed file");
    }

    // ----- File-system changes ----------------------------------------------------------

    private void OnFilesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _index.Invalidate(_context.Root);
        if (!_isActive || !_treeLoaded)
        {
            _treeStale = _treeLoaded;
            return;
        }

        _changeDebouncer ??= new Debouncer(ChangeDebounce);
        _changeDebouncer.Trigger(() => _services.Dispatcher.InvokeAsync(() => _ = RefreshAfterChangeAsync()));
    }

    private async Task RefreshAfterChangeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_isActive)
        {
            _treeStale = true;
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
        await Viewer.ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>The refresh triggered by the last file-system change (tests wait on it).</summary>
    internal Task WhenIdleAsync() => _reloadGate.WaitAsync().ContinueWith(_ => _reloadGate.Release(), TaskScheduler.Default);

    // ----- Go to file -------------------------------------------------------------------

    partial void OnGoToTextChanged(string value)
    {
        CancelGoTo();
        if (string.IsNullOrWhiteSpace(value))
        {
            _goToRows = null;
            GoToError = null;
            IsGoToSearching = false;
            UpdateRows();
            return;
        }

        if (_disposed || _context.Lifetime.IsCancellationRequested)
        {
            return;
        }

        _goTo = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        PendingGoTo = RunGoToAsync(value.Trim(), _goTo.Token);
    }

    /// <summary>The "Go to file" search in flight (tests wait on it).</summary>
    internal Task PendingGoTo { get; private set; } = Task.CompletedTask;

    /// <summary>Delay after a keystroke before the index is searched.</summary>
    public TimeSpan GoToDelay { get; init; } = TimeSpan.FromMilliseconds(60);

    private async Task RunGoToAsync(string text, CancellationToken cancellationToken)
    {
        IsGoToSearching = true;
        try
        {
            if (GoToDelay > TimeSpan.Zero)
            {
                await Task.Delay(GoToDelay, cancellationToken).ConfigureAwait(true);
            }

            var snapshot = await _index.GetAsync(_context.Root, false, cancellationToken).ConfigureAwait(true);
            var matches = await Task.Run(() => _index.Search(snapshot, text, MaxGoToResults), cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _goToRows = matches.Select(m =>
            {
                var node = FileTreeNodeViewModel.ForFlatFile(m.RelativePath.Replace('\\', '/'));
                node.GitState = _decorations.StateOf(node.RelativePath);
                return node;
            }).ToList();
            GoToError = null;
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            return;
        }
        catch (Exception ex)
        {
            _goToRows = [];
            GoToError = ErrorInfo.From(ex).Message;
        }

        IsGoToSearching = false;
        UpdateRows();
    }

    /// <summary>Enter in the "Go to file" box: opens the selected (or first) result in the tree.</summary>
    [RelayCommand]
    private Task AcceptGoToAsync()
    {
        var target = SelectedNode is { IsFlat: true } selected && _goToRows?.Contains(selected) == true ? selected : _goToRows?.FirstOrDefault();
        return target is null ? Task.CompletedTask : RevealAsync(new FileLocation(target.RelativePath));
    }

    [RelayCommand(CanExecute = nameof(IsGoToActive))]
    private void ClearGoTo() => GoToText = string.Empty;

    /// <summary>Focuses the "Go to file" box (Ctrl+P while the tab has focus, toolbar).</summary>
    [RelayCommand]
    private void FocusGoTo()
    {
        IsSearchOpen = false;
        FocusGoToRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGoTo()
    {
        var goTo = _goTo;
        _goTo = null;
        try
        {
            goTo?.Cancel();
            goTo?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ----- Search in files --------------------------------------------------------------

    /// <summary>Ctrl+Shift+F: shows the search panel and focuses its query.</summary>
    [RelayCommand]
    private void OpenSearch()
    {
        IsSearchOpen = true;
        Search.RequestFocus();
    }

    /// <summary>Esc in the search panel: back to the tree.</summary>
    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private void ToggleSearch()
    {
        if (IsSearchOpen)
        {
            CloseSearch();
        }
        else
        {
            OpenSearch();
        }
    }

    private Task OpenFromSearchAsync(FileLocation location) => Viewer.LoadAsync(location.RelativePath, location.Line);

    // ----- Navigation -------------------------------------------------------------------

    /// <summary>Expands the tree down to a file or folder, selects it and shows it (at a line).</summary>
    public async Task RevealAsync(FileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (_disposed)
        {
            return;
        }

        if (!_treeLoaded)
        {
            await LoadTreeAsync().ConfigureAwait(true);
        }

        IsSearchOpen = false;
        if (IsGoToActive)
        {
            GoToText = string.Empty;
        }

        var path = location.RelativePath;
        if (OnlyChangedFiles && _decorations.StateOf(path) == FileGitState.None)
        {
            OnlyChangedFiles = false;
        }

        if (path.Length == 0)
        {
            SelectQuietly(null);
            Viewer.ShowFolder(string.Empty, ProjectName, _roots.Count(n => n.IsDirectory), _roots.Count(n => !n.IsDirectory));
            return;
        }

        if (!ShowHiddenFiles && IsHiddenPath(path))
        {
            ShowHiddenFiles = true;
        }

        var node = OnlyChangedFiles ? _changedRows.FirstOrDefault(n => string.Equals(n.RelativePath, path, StringComparison.Ordinal)) : await ExpandToAsync(path).ConfigureAwait(true);
        UpdateRows();
        if (node is not null && Rows.Contains(node))
        {
            SelectQuietly(node);
            RevealRequested?.Invoke(this, node);
            ShowInViewer(node, location.Line);
            return;
        }

        // Not in the tree (filtered, or gone): still show it.
        SelectQuietly(null);
        _ = Viewer.LoadAsync(path, location.Line);
    }

    /// <summary>Breadcrumb segment click: reveals that folder (or the file) in the tree.</summary>
    [RelayCommand]
    private Task NavigateBreadcrumbAsync(BreadcrumbSegment? segment) =>
        segment is null ? Task.CompletedTask : RevealAsync(new FileLocation(segment.RelativePath));

    private async Task<FileTreeNodeViewModel?> ExpandToAsync(string path)
    {
        var level = _roots;
        FileTreeNodeViewModel? found = null;
        foreach (var part in FileLocation.AncestorsOf(path).Append(path))
        {
            found = level.FirstOrDefault(n => string.Equals(n.RelativePath, part, PathUtil.Comparison));
            if (found is null)
            {
                return null;
            }

            if (ReferenceEquals(part, path) || string.Equals(part, path, StringComparison.Ordinal))
            {
                break;
            }

            if (!found.IsDirectory)
            {
                return null;
            }

            if (!found.IsLoaded || found.IsStale)
            {
                await LoadChildrenAsync(found).ConfigureAwait(true);
            }

            found.IsExpanded = true;
            level = found.Children ?? [];
        }

        return found;
    }

    private void SelectQuietly(FileTreeNodeViewModel? node)
    {
        _quietSelection = true;
        try
        {
            SelectedNode = node;
        }
        finally
        {
            _quietSelection = false;
        }
    }

    // ----- Item actions (context menu, toolbar) ----------------------------------------

    [RelayCommand]
    private void OpenInEditor(FileTreeNodeViewModel? node)
    {
        if (Target(node) is not { } target || FullPathOf(target) is not { } path)
        {
            return;
        }

        Try(() =>
        {
            if (target.IsDirectory)
            {
                _services.Shell.OpenFolderInEditor(path);
            }
            else
            {
                _services.Shell.OpenInEditor(path, ReferenceEquals(target, SelectedNode) && Viewer.IsText ? Viewer.ScrollToLine : null);
            }
        }, "Could not open the code editor");
    }

    [RelayCommand]
    private void OpenWithDefaultApp(FileTreeNodeViewModel? node)
    {
        if (Target(node) is not { } target || FullPathOf(target) is not { } path)
        {
            return;
        }

        Try(() =>
        {
            if (target.IsDirectory)
            {
                _services.Shell.OpenFolder(path);
            }
            else
            {
                _services.Shell.OpenWithDefaultApp(path);
            }
        }, "Could not open the file");
    }

    [RelayCommand]
    private void RevealInExplorer(FileTreeNodeViewModel? node)
    {
        if (Target(node) is { } target && FullPathOf(target) is { } path)
        {
            Try(() => _services.Shell.RevealInExplorer(path), "Could not open File Explorer");
        }
    }

    [RelayCommand]
    private void CopyPath(FileTreeNodeViewModel? node)
    {
        if (Target(node) is { } target && FullPathOf(target) is { } path)
        {
            Try(() =>
            {
                _services.Shell.CopyToClipboard(path);
                _services.Notifications.Show("Path copied", path);
            }, "Could not copy the path");
        }
    }

    [RelayCommand]
    private void CopyRelativePath(FileTreeNodeViewModel? node)
    {
        if (Target(node) is { } target)
        {
            Try(() =>
            {
                _services.Shell.CopyToClipboard(target.RelativePath);
                _services.Notifications.Show("Relative path copied", target.RelativePath);
            }, "Could not copy the path");
        }
    }

    /// <summary>Shows the file's changes (or history, when unchanged) in the Git tab.</summary>
    [RelayCommand]
    private void ShowChanges(FileTreeNodeViewModel? node)
    {
        if (Target(node) is { } target)
        {
            _context.RequestNavigation(WorkspaceSection.Git, target.RelativePath);
        }
    }

    /// <summary>Opens a new terminal in the folder (the file's folder for a file).</summary>
    [RelayCommand]
    private void OpenTerminalHere(FileTreeNodeViewModel? node)
    {
        var target = Target(node);
        var folder = target is null ? string.Empty : target.IsDirectory ? target.RelativePath : FileLocation.ParentOf(target.RelativePath);
        _context.RequestNavigation(WorkspaceSection.Terminal, folder);
    }

    /// <summary>Opens "Search in files" limited to the folder.</summary>
    [RelayCommand]
    private void SearchInFolder(FileTreeNodeViewModel? node)
    {
        var target = Target(node);
        var folder = target is null ? string.Empty : target.IsDirectory ? target.RelativePath : FileLocation.ParentOf(target.RelativePath);
        IsSearchOpen = true;
        Search.ScopeTo(folder);
    }

    private FileTreeNodeViewModel? Target(FileTreeNodeViewModel? node) => node ?? SelectedNode;

    private string? FullPathOf(FileTreeNodeViewModel node)
    {
        try
        {
            return node.RelativePath.Length == 0 ? _context.Root : PathUtil.ResolveUnder(_context.Root, node.RelativePath);
        }
        catch (Exception ex) when (ex is ForgeException or ArgumentException)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Invalid path"));
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
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowRows)));
        }
    }
}
