using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// The application services a project workspace works with (git, runs, tasks, dialogs…), grouped
/// so each open project is created with one dependency instead of fifteen.
/// </summary>
public sealed class WorkspaceServices
{
    public WorkspaceServices(
        IWorkspaceSectionFactory sections,
        IGitService git,
        IProjectRegistry registry,
        IProjectStatusService status,
        IRunService runs,
        IWorkItemService workItems,
        IActivityLog activity,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        INavigationService navigation,
        IProjectActions projectActions,
        IUiDispatcher dispatcher,
        ILoggerFactory? loggerFactory = null)
    {
        Sections = sections;
        Git = git;
        Registry = registry;
        Status = status;
        Runs = runs;
        WorkItems = workItems;
        Activity = activity;
        Settings = settings;
        Dialogs = dialogs;
        Notifications = notifications;
        Shell = shell;
        Navigation = navigation;
        ProjectActions = projectActions;
        Dispatcher = dispatcher;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IWorkspaceSectionFactory Sections { get; }
    public IGitService Git { get; }
    public IProjectRegistry Registry { get; }
    public IProjectStatusService Status { get; }
    public IRunService Runs { get; }
    public IWorkItemService WorkItems { get; }
    public IActivityLog Activity { get; }
    public ISettingsService Settings { get; }
    public IDialogService Dialogs { get; }
    public INotificationService Notifications { get; }
    public IShellIntegration Shell { get; }
    public INavigationService Navigation { get; }
    public IProjectActions ProjectActions { get; }
    public IUiDispatcher Dispatcher { get; }
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Delay before the first automatic fetch after a project opens.</summary>
    public TimeSpan InitialAutoFetchDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Clock used for timestamps shown to the user.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
