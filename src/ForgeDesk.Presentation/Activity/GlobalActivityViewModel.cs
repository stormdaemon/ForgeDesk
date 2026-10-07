using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Activity;

/// <summary>The global Activity page: the journal of every project, each entry showing its project.</summary>
public sealed class GlobalActivityViewModel : INavigationAware, IRefreshable, IDisposable
{
    private readonly IProjectRegistry _registry;
    private readonly IProjectActions _projectActions;
    private readonly IUiDispatcher _dispatcher;
    private Dictionary<string, ActivityProjectInfo> _projects = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _disposed;

    public GlobalActivityViewModel(
        IActivityLog log,
        IProjectRegistry registry,
        IProjectActions projectActions,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        IUiDispatcher dispatcher,
        ActivityTimelineOptions? options = null)
    {
        _registry = registry;
        _projectActions = projectActions;
        _dispatcher = dispatcher;
        Timeline = new ActivityTimelineViewModel(log, dialogs, notifications, shell, dispatcher,
            (options ?? new ActivityTimelineOptions()) with { ProjectId = null, IsGlobal = true },
            OpenTargetAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _registry.Changed += OnProjectsChanged;
    }

    public ActivityTimelineViewModel Timeline { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (_loaded || _disposed)
        {
            return;
        }

        _loaded = true;
        await LoadProjectsAsync().ConfigureAwait(true);
        await Timeline.LoadAsync().ConfigureAwait(true);
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task RefreshAsync()
    {
        _loaded = true;
        await LoadProjectsAsync().ConfigureAwait(true);
        await Timeline.LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            var projects = await _registry.GetAllAsync().ConfigureAwait(true);
            _projects = projects.ToDictionary(p => p.Id, p => new ActivityProjectInfo(p.Name, p.Color), StringComparer.Ordinal);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // Entries still render without project names; following them is disabled until the next refresh.
            System.Diagnostics.Trace.TraceWarning($"Could not load the projects for the activity page: {ex.Message}");
        }

        if (!_disposed)
        {
            Timeline.SetProjectLookup(id => _projects.GetValueOrDefault(id));
        }
    }

    private void OnProjectsChanged(object? sender, ProjectsChangedEventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (_loaded && !_disposed)
            {
                _ = LoadProjectsAsync();
            }
        });

    private async Task OpenTargetAsync(ActivityTarget target)
    {
        if (target.ProjectId is not { } projectId)
        {
            return;
        }

        if (target.Kind == ActivityTargetKind.Project)
        {
            await _projectActions.OpenAsync(projectId, WorkspaceSection.Overview).ConfigureAwait(true);
        }
        else if (target is { Kind: ActivityTargetKind.Section, Section: { } section })
        {
            await _projectActions.OpenAsync(projectId, section, target.Argument).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Changed -= OnProjectsChanged;
        Timeline.Dispose();
    }
}
