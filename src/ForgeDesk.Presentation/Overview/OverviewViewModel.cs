using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>
/// The Overview tab, the default tab of a project: the project at a glance as actionable cards
/// (working tree, recent commits, CI, commands, tasks, attention, recent activity).
/// </summary>
public sealed partial class OverviewViewModel : ViewModelBase, IWorkspaceSectionViewModel, IRefreshable, IDisposable
{
    private readonly IGitHubService _gitHub;
    private readonly IProjectRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private bool _disposed;

    public OverviewViewModel(
        ProjectContext context,
        IGitService git,
        IProjectStatusService status,
        IGitHubService gitHub,
        IGitHubAccountService accounts,
        IRunService runs,
        IWorkItemService workItems,
        IActivityLog activity,
        IFileService files,
        IProjectRegistry registry,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        INavigationService navigation,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        _gitHub = gitHub;
        _registry = registry;
        _dialogs = dialogs;
        _notifications = notifications;

        Hero = new OverviewHeroViewModel(context, files, shell, notifications);
        WorkingTree = new OverviewWorkingTreeViewModel(context);
        Commits = new OverviewCommitsViewModel(context, git, shell, notifications);
        Ci = new OverviewCiViewModel(context, status, gitHub, accounts, shell, navigation, notifications, dispatcher);
        Commands = new OverviewCommandsViewModel(context, runs, notifications, dispatcher);
        Tasks = new OverviewTasksViewModel(context, workItems, dialogs, notifications, dispatcher);
        Attention = new OverviewAttentionViewModel(context, status, dispatcher);
        Activity = new OverviewActivityViewModel(context, activity, shell, notifications, dispatcher);
        Cards = [Hero, WorkingTree, Commits, Ci, Commands, Tasks, Attention, Activity];

        Context.GitStatusChanged += OnGitStatusChanged;
        Context.PropertyChanged += OnContextPropertyChanged;
        UpdateFolderState();
    }

    public WorkspaceSection Section => WorkspaceSection.Overview;

    public ProjectContext Context { get; }

    public string Name => Context.Project.Name;

    public OverviewHeroViewModel Hero { get; }

    public OverviewWorkingTreeViewModel WorkingTree { get; }

    public OverviewCommitsViewModel Commits { get; }

    public OverviewCiViewModel Ci { get; }

    public OverviewCommandsViewModel Commands { get; }

    public OverviewTasksViewModel Tasks { get; }

    public OverviewAttentionViewModel Attention { get; }

    public OverviewActivityViewModel Activity { get; }

    public IReadOnlyList<OverviewCardViewModel> Cards { get; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>The project folder no longer exists: the cards are replaced by a "Locate folder" state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    public partial bool IsFolderMissing { get; private set; }

    public bool ShowContent => !IsFolderMissing;

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = true;
        foreach (var card in Cards)
        {
            card.IsActive = true;
        }

        UpdateFolderState();
        await Task.WhenAll(Cards.Where(c => !c.HasLoaded || c.IsStale).Select(c => c.LoadAsync())).ConfigureAwait(true);
    }

    public void Deactivate()
    {
        IsActive = false;
        foreach (var card in Cards)
        {
            card.IsActive = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        UpdateFolderState();
        var linked = Context.Project.GitHub is { } repo && _gitHub.IsSignedIn;
        if (linked)
        {
            _gitHub.InvalidateCache(Context.Project.GitHub);
        }

        await Task.WhenAll(Cards.Select(c => c.LoadAsync()).Append(Attention.RecomputeAsync(includeRemote: linked))).ConfigureAwait(true);
    }

    /// <summary>"Locate folder…" of the missing-folder state.</summary>
    [RelayCommand]
    private async Task LocateFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync($"Locate the folder of {Context.Project.Name}").ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the folder picker"));
            return;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var project = await _registry.RelocateAsync(Context.ProjectId, folder, Context.Lifetime).ConfigureAwait(true);
            Context.Project = project;
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
            _ = Context.RefreshProfileAsync();
            _notifications.Show($"Found {project.Name}", project.Path, NotificationSeverity.Success);
            UpdateFolderState();
            await Task.WhenAll(Cards.Select(c => c.LoadAsync())).ConfigureAwait(true);
        }, errorTitle: "Could not use this folder", errorMode: ErrorMode.Toast, notifications: _notifications).ConfigureAwait(true);
    }

    private void UpdateFolderState() => IsFolderMissing = Context.IsRepositoryChecked && !Context.FolderExists;

    private void OnGitStatusChanged(object? sender, EventArgs e) => UpdateFolderState();

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectContext.Project))
        {
            OnPropertyChanged(nameof(Name));
            UpdateFolderState();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Context.GitStatusChanged -= OnGitStatusChanged;
        Context.PropertyChanged -= OnContextPropertyChanged;
        foreach (var card in Cards)
        {
            card.Dispose();
        }
    }
}
