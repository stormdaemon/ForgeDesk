using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Activity;

/// <summary>The Activity tab of a project: the journal filtered to this project.</summary>
public sealed class ActivitySectionViewModel : IWorkspaceSectionViewModel, IRefreshable, IDisposable
{
    private readonly ProjectContext _context;
    private bool _loaded;

    public ActivitySectionViewModel(
        ProjectContext context,
        IActivityLog log,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        IUiDispatcher dispatcher,
        ActivityTimelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Timeline = new ActivityTimelineViewModel(log, dialogs, notifications, shell, dispatcher,
            (options ?? new ActivityTimelineOptions()) with { ProjectId = context.ProjectId, IsGlobal = false },
            OpenTargetAsync);
    }

    public WorkspaceSection Section => WorkspaceSection.Activity;

    public ActivityTimelineViewModel Timeline { get; }

    public IAsyncRelayCommand RefreshCommand => Timeline.RefreshCommand;

    public async Task ActivateAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await Timeline.LoadAsync().ConfigureAwait(true);
    }

    public void Deactivate()
    {
    }

    private Task OpenTargetAsync(ActivityTarget target)
    {
        if (target is { Kind: ActivityTargetKind.Section, Section: { } section })
        {
            _context.RequestNavigation(section, target.Argument);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => Timeline.Dispose();
}
