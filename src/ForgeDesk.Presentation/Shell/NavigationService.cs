using System.ComponentModel;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// Page stack of the main window. Top-level pages are created once through <see cref="IPageFactory"/>
/// (unregistered ones are simply unavailable); each open project keeps one cached workspace, watched
/// for file changes, until <see cref="CloseProject"/>. Call from the UI thread.
/// </summary>
public sealed class NavigationService : INavigationService, IDisposable
{
    private const int MaxHistory = 50;

    private readonly IPageFactory _pageFactory;
    private readonly IProjectWorkspaceFactory _workspaceFactory;
    private readonly IProjectRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly IProjectWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<NavigationService> _logger;
    private readonly Dictionary<PageKind, object> _pages = [];
    private readonly Dictionary<string, OpenProject> _projects = new(StringComparer.Ordinal);
    private readonly List<NavigationEntry> _history = [];
    private NavigationEntry? _current;
    private bool _disposed;

    public NavigationService(
        IPageFactory pageFactory,
        IProjectWorkspaceFactory workspaceFactory,
        IProjectRegistry registry,
        ISettingsService settings,
        IProjectWatcher watcher,
        IUiDispatcher dispatcher,
        ILogger<NavigationService> logger)
    {
        _pageFactory = pageFactory;
        _workspaceFactory = workspaceFactory;
        _registry = registry;
        _settings = settings;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _logger = logger;
        _watcher.Changed += OnFilesChanged;
        _registry.Changed += OnRegistryChanged;
    }

    public object? CurrentPage { get; private set; }

    public PageKind CurrentKind => _current?.Kind ?? PageKind.Dashboard;

    public string? CurrentProjectId => _current?.ProjectId;

    public bool CanGoBack => _history.Count > 0;

    /// <summary>The workspace on screen, if the current page is a project.</summary>
    public ProjectWorkspaceViewModel? CurrentWorkspace => CurrentPage as ProjectWorkspaceViewModel;

    /// <summary>Workspaces of the projects opened in this session (not yet closed).</summary>
    public IReadOnlyCollection<ProjectWorkspaceViewModel> OpenWorkspaces => _projects.Values.Select(p => p.Workspace).ToArray();

    public event EventHandler? Navigated;

    public bool IsAvailable(PageKind kind) => kind == PageKind.Project || _pageFactory.IsAvailable(kind);

    public void GoToDashboard() => NavigateToPage(PageKind.Dashboard, null);

    public void OpenSettings(string? section = null) => NavigateToPage(PageKind.Settings, section);

    public void OpenActivity() => NavigateToPage(PageKind.Activity, null);

    public void OpenOnboarding() => NavigateToPage(PageKind.Onboarding, null);

    public async Task OpenProjectAsync(string projectId, WorkspaceSection? section = null, object? argument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workspace = await GetOrCreateWorkspaceAsync(projectId).ConfigureAwait(true);
        var request = section is { } target ? new WorkspaceNavigationRequest(target, argument) : null;
        var entry = new NavigationEntry(PageKind.Project, projectId);
        var activation = _current == entry && ReferenceEquals(CurrentPage, workspace)
            ? ActivatePageAsync(workspace, request)
            : Show(entry, workspace, request, addToHistory: true);

        await RememberOpenedAsync(projectId).ConfigureAwait(true);
        await activation.ConfigureAwait(true);
    }

    public void CloseProject(string projectId)
    {
        if (!_projects.Remove(projectId, out var open))
        {
            return;
        }

        open.Workspace.Context.PropertyChanged -= OnContextPropertyChanged;
        Unwatch(open);
        _history.RemoveAll(e => string.Equals(e.ProjectId, projectId, StringComparison.Ordinal));

        if (_current is { } current && string.Equals(current.ProjectId, projectId, StringComparison.Ordinal))
        {
            // Leave the page before disposing it so the view never renders a disposed workspace.
            _current = null;
            CurrentPage = null;
            if (!TryGoBack() && !TryShowPage(PageKind.Dashboard, null, addToHistory: false))
            {
                Navigated?.Invoke(this, EventArgs.Empty);
            }
        }

        DisposeWorkspace(open.Workspace);
    }

    public void GoBack() => TryGoBack();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Changed -= OnFilesChanged;
        _registry.Changed -= OnRegistryChanged;
        foreach (var open in _projects.Values)
        {
            open.Workspace.Context.PropertyChanged -= OnContextPropertyChanged;
            Unwatch(open);
            DisposeWorkspace(open.Workspace);
        }

        _projects.Clear();
        foreach (var page in _pages.Values.OfType<IDisposable>())
        {
            try
            {
                page.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A page failed to dispose");
            }
        }

        _pages.Clear();
    }

    private void NavigateToPage(PageKind kind, object? argument)
    {
        if (_disposed)
        {
            return;
        }

        if (_current is { } current && current.Kind == kind && CurrentPage is { } page)
        {
            // Already there: only pass the new argument (e.g. another settings section).
            if (argument is not null)
            {
                _ = ActivatePageAsync(page, argument);
            }

            return;
        }

        if (!TryShowPage(kind, argument, addToHistory: true))
        {
            _logger.LogWarning("The {Page} page is not available; staying on the current page", kind);
        }
    }

    private bool TryShowPage(PageKind kind, object? argument, bool addToHistory)
    {
        var page = GetPage(kind);
        if (page is null)
        {
            return false;
        }

        _ = Show(new NavigationEntry(kind, null), page, argument, addToHistory);
        return true;
    }

    private bool TryGoBack()
    {
        while (_history.Count > 0)
        {
            var entry = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            var page = entry.Kind == PageKind.Project
                ? (entry.ProjectId is { } id && _projects.TryGetValue(id, out var open) ? open.Workspace : null)
                : GetPage(entry.Kind);
            if (page is not null)
            {
                _ = Show(entry, page, null, addToHistory: false);
                return true;
            }
        }

        return false;
    }

    private Task Show(NavigationEntry entry, object page, object? argument, bool addToHistory)
    {
        var previous = CurrentPage;
        if (addToHistory && _current is { } current && current != entry && current.Kind != PageKind.Onboarding)
        {
            _history.Add(current);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        _current = entry;
        CurrentPage = page;
        if (previous is INavigationAware leaving && !ReferenceEquals(previous, page))
        {
            try
            {
                leaving.OnNavigatedFrom();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Page} failed while being left", previous.GetType().Name);
            }
        }

        Navigated?.Invoke(this, EventArgs.Empty);
        return ActivatePageAsync(page, argument);
    }

    private async Task ActivatePageAsync(object page, object? argument)
    {
        if (page is not INavigationAware aware)
        {
            return;
        }

        try
        {
            await aware.OnNavigatedToAsync(argument).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Page} failed to load", page.GetType().Name);
        }
    }

    private object? GetPage(PageKind kind)
    {
        if (_pages.TryGetValue(kind, out var cached))
        {
            return cached;
        }

        try
        {
            var page = _pageFactory.Create(kind);
            if (page is not null)
            {
                _pages[kind] = page;
            }

            return page;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The {Page} page could not be created", kind);
            return null;
        }
    }

    private async Task<ProjectWorkspaceViewModel> GetOrCreateWorkspaceAsync(string projectId)
    {
        if (_projects.TryGetValue(projectId, out var open))
        {
            return open.Workspace;
        }

        var project = await _registry.GetAsync(projectId).ConfigureAwait(true)
            ?? throw new ForgeException(ErrorKind.NotFound, "This project is no longer registered in ForgeDesk.",
                "It may have been removed. Add its folder again to open it.");

        // Another call may have opened it while the registry was read.
        if (_projects.TryGetValue(projectId, out open))
        {
            return open.Workspace;
        }

        var workspace = _workspaceFactory.Create(project);
        open = new OpenProject(workspace);
        _projects.Add(projectId, open);
        workspace.Context.PropertyChanged += OnContextPropertyChanged;
        Watch(open);
        return workspace;
    }

    private async Task RememberOpenedAsync(string projectId)
    {
        try
        {
            await _registry.MarkOpenedAsync(projectId).ConfigureAwait(true);
            if (!string.Equals(_settings.Current.LastOpenedProjectId, projectId, StringComparison.Ordinal))
            {
                await _settings.UpdateAsync(s => s with { LastOpenedProjectId = projectId }).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record that project {ProjectId} was opened", projectId);
        }
    }

    private void Watch(OpenProject open)
    {
        var root = open.Workspace.Context.Root;
        try
        {
            if (Directory.Exists(root))
            {
                _watcher.Watch(root);
                open.WatchedRoot = root;
            }
        }
        catch (Exception ex)
        {
            // Without a watcher the workspace still works; it refreshes on focus and on F5.
            _logger.LogWarning(ex, "Could not watch {Root} for changes", root);
        }
    }

    private void Unwatch(OpenProject open)
    {
        if (open.WatchedRoot is not { } root)
        {
            return;
        }

        open.WatchedRoot = null;
        try
        {
            _watcher.Unwatch(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop watching {Root}", root);
        }
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectContext.Project) || sender is not ProjectContext context)
        {
            return;
        }

        var open = _projects.Values.FirstOrDefault(p => ReferenceEquals(p.Workspace.Context, context));
        if (open is not null && (open.WatchedRoot is null || !IsSamePath(open.WatchedRoot, context.Root)))
        {
            // The project was relocated: follow its new folder.
            Unwatch(open);
            Watch(open);
        }
    }

    private void OnFilesChanged(object? sender, ProjectFilesChangedEventArgs e) => _dispatcher.Post(() =>
    {
        foreach (var open in _projects.Values)
        {
            if (open.WatchedRoot is { } root && IsSamePath(root, e.ProjectRoot))
            {
                open.Workspace.Context.NotifyFilesChanged(e.GitMetadataChanged);
            }
        }
    });

    private void OnRegistryChanged(object? sender, ProjectsChangedEventArgs e) =>
        _dispatcher.Post(() => _ = SyncOpenProjectsAsync(e.ProjectId));

    /// <summary>Keeps open workspaces in step with the registry (renamed, recolored, relocated or removed projects).</summary>
    private async Task SyncOpenProjectsAsync(string? projectId)
    {
        var ids = projectId is null ? _projects.Keys.ToArray() : [projectId];
        foreach (var id in ids)
        {
            if (!_projects.TryGetValue(id, out var open))
            {
                continue;
            }

            Project? project;
            try
            {
                project = await _registry.GetAsync(id).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reload project {ProjectId}", id);
                continue;
            }

            if (!_projects.TryGetValue(id, out var stillOpen) || !ReferenceEquals(stillOpen, open))
            {
                continue;
            }

            if (project is null)
            {
                CloseProject(id);
            }
            else if (project != open.Workspace.Context.Project)
            {
                open.Workspace.Context.Project = project;
            }
        }
    }

    private void DisposeWorkspace(ProjectWorkspaceViewModel workspace)
    {
        try
        {
            workspace.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The workspace of {ProjectId} failed to close cleanly", workspace.ProjectId);
        }
    }

    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return PathUtil.AreSame(a, b);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private readonly record struct NavigationEntry(PageKind Kind, string? ProjectId);

    private sealed class OpenProject(ProjectWorkspaceViewModel workspace)
    {
        public ProjectWorkspaceViewModel Workspace { get; } = workspace;

        public string? WatchedRoot { get; set; }
    }
}
