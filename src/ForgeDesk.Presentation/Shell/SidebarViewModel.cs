using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// Left navigation: Home, Activity, then PINNED and RECENT projects with their live status. Cached
/// snapshots render first; all projects are then refreshed in the background (three at a time),
/// every five minutes, and whenever the registry, the status service or a run reports a change.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    public const int MaxRecentProjects = 10;

    private readonly IProjectRegistry _registry;
    private readonly IProjectStatusService _status;
    private readonly IRunService _runs;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly IProjectActions _actions;
    private readonly ILogger<SidebarViewModel> _logger;
    private readonly Dictionary<string, ProjectNavItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Debouncer _reloadDebouncer = new(TimeSpan.FromMilliseconds(150));
    private CancellationTokenSource? _refreshLoop;
    private bool _initialized;
    private bool _disposed;

    public SidebarViewModel(
        IProjectRegistry registry,
        IProjectStatusService status,
        IRunService runs,
        INavigationService navigation,
        IPageFactory pages,
        IProjectActions actions,
        IUiDispatcher dispatcher,
        ILogger<SidebarViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _registry = registry;
        _status = status;
        _runs = runs;
        _navigation = navigation;
        _actions = actions;
        _dispatcher = dispatcher;
        _logger = logger;

        Home = new ShellNavItemViewModel(PageKind.Dashboard, "Home", "Home20", pages.IsAvailable(PageKind.Dashboard), navigation.GoToDashboard);
        Activity = new ShellNavItemViewModel(PageKind.Activity, "Activity", "History20", pages.IsAvailable(PageKind.Activity), navigation.OpenActivity);

        _navigation.Navigated += OnNavigated;
        _registry.Changed += OnRegistryChanged;
        _status.SnapshotUpdated += OnSnapshotUpdated;
        _runs.RunStarted += OnRunStarted;
        _runs.RunCompleted += OnRunCompleted;
    }

    public ShellNavItemViewModel Home { get; }

    public ShellNavItemViewModel Activity { get; }

    public ObservableCollection<ProjectNavItemViewModel> PinnedProjects { get; } = [];

    public ObservableCollection<ProjectNavItemViewModel> RecentProjects { get; } = [];

    public bool HasPinned => PinnedProjects.Count > 0;

    public bool HasRecent => RecentProjects.Count > 0;

    public bool HasProjects => _items.Count > 0;

    /// <summary>Icon rail (56 px) instead of the full 240 px sidebar. Kept for the session only.</summary>
    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    /// <summary>Interval of the background status refresh.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many projects are refreshed at the same time.</summary>
    public int RefreshConcurrency { get; init; } = 3;

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    /// <summary>Loads the projects with their cached status, then starts the background refresh.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        await ReloadAsync().ConfigureAwait(true);
        _refreshLoop = new CancellationTokenSource();
        _ = RunRefreshLoopAsync(_refreshLoop.Token);
    }

    /// <summary>Re-reads the registry and rebuilds both lists (entries keep their status).</summary>
    public async Task ReloadAsync()
    {
        IReadOnlyList<Project> projects;
        try
        {
            projects = await _registry.GetAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                _logger.LogWarning(ex, "Could not load the project list");
            }

            return;
        }

        if (_disposed)
        {
            return;
        }

        var added = new List<ProjectNavItemViewModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            ids.Add(project.Id);
            if (_items.TryGetValue(project.Id, out var item))
            {
                item.Update(project);
            }
            else
            {
                item = new ProjectNavItemViewModel(project, _actions);
                _items.Add(project.Id, item);
                added.Add(item);
            }
        }

        foreach (var removed in _items.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _items.Remove(removed);
        }

        var (pinned, recent) = Arrange(_items.Values.Select(i => i.Project));
        PinnedProjects.SyncWith(pinned.Select(p => _items[p.Id]).ToList());
        RecentProjects.SyncWith(recent.Select(p => _items[p.Id]).ToList());
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasProjects));
        UpdateCurrent();
        UpdateRunning();

        foreach (var item in added)
        {
            await ApplyCachedSnapshotAsync(item).ConfigureAwait(true);
        }

        // Projects that appeared after startup (added, cloned) get a fresh status right away.
        if (_refreshLoop is { IsCancellationRequested: false } loop && added.Count > 0)
        {
            _ = RefreshAsync(added.Select(i => i.Project).ToArray(), loop.Token);
        }
    }

    /// <summary>Recomputes the status of every project, <see cref="RefreshConcurrency"/> at a time.</summary>
    public Task RefreshStatusesAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(_items.Values.Select(i => i.Project).ToArray(), cancellationToken);

    /// <summary>Pinned projects (by sort order, then name) and the most recently opened others.</summary>
    public static (IReadOnlyList<Project> Pinned, IReadOnlyList<Project> Recent) Arrange(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var all = projects.ToList();
        var pinned = all.Where(p => p.IsPinned)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var recent = all.Where(p => !p.IsPinned)
            .OrderByDescending(p => p.LastOpenedAt.HasValue)
            .ThenByDescending(p => p.LastOpenedAt ?? p.AddedAt)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxRecentProjects)
            .ToList();
        return (pinned, recent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshLoop?.Cancel();
        _refreshLoop?.Dispose();
        _refreshLoop = null;
        _reloadDebouncer.Dispose();
        _navigation.Navigated -= OnNavigated;
        _registry.Changed -= OnRegistryChanged;
        _status.SnapshotUpdated -= OnSnapshotUpdated;
        _runs.RunStarted -= OnRunStarted;
        _runs.RunCompleted -= OnRunCompleted;
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshStatusesAsync(cancellationToken).ConfigureAwait(true);
                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, RefreshConcurrency));
        try
        {
            await Task.WhenAll(projects.Select(p => RefreshOneAsync(p, gate, cancellationToken))).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshOneAsync(Project project, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _status.RefreshAsync(project, includeRemote: true, cancellationToken).ConfigureAwait(false);
            _dispatcher.Post(() => Apply(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable project (network drive, locked repository) must not stop the others.
            _logger.LogDebug(ex, "Could not refresh the status of {ProjectId}", project.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyCachedSnapshotAsync(ProjectNavItemViewModel item)
    {
        try
        {
            if (await _status.GetCachedAsync(item.Id).ConfigureAwait(true) is { } snapshot && !item.HasSnapshot)
            {
                item.Apply(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No cached status for {ProjectId}", item.Id);
        }
    }

    private void Apply(ProjectSnapshot snapshot)
    {
        if (!_disposed && _items.TryGetValue(snapshot.ProjectId, out var item))
        {
            item.Apply(snapshot);
        }
    }

    private void UpdateCurrent()
    {
        var kind = _navigation.CurrentKind;
        var hasPage = _navigation.CurrentPage is not null;
        Home.IsCurrent = hasPage && kind == PageKind.Dashboard;
        Activity.IsCurrent = hasPage && kind == PageKind.Activity;
        var currentProject = kind == PageKind.Project ? _navigation.CurrentProjectId : null;
        foreach (var item in _items.Values)
        {
            item.IsCurrent = string.Equals(item.Id, currentProject, StringComparison.Ordinal);
        }
    }

    private void UpdateRunning()
    {
        var running = _runs.ActiveRuns
            .Where(r => r.Status is RunStatus.Running or RunStatus.Queued)
            .Select(r => r.Request.ProjectId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in _items.Values)
        {
            item.IsRunning = running.Contains(item.Id);
        }
    }

    private void OnNavigated(object? sender, EventArgs e) => UpdateCurrent();

    private void OnRegistryChanged(object? sender, ProjectsChangedEventArgs e) =>
        _reloadDebouncer.Trigger(() => _dispatcher.InvokeAsync(() => _ = ReloadAsync()));

    private void OnSnapshotUpdated(object? sender, ProjectSnapshot snapshot) => _dispatcher.Post(() => Apply(snapshot));

    private void OnRunStarted(object? sender, IRunSession session) => _dispatcher.Post(UpdateRunning);

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e) => _dispatcher.Post(UpdateRunning);
}
