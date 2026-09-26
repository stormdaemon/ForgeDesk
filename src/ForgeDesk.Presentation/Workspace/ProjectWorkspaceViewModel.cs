using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// The page shown for an open project: header (identity, branch, sync, quick commands, "Open in"),
/// banners (missing folder, git error) and the tab strip whose sections are created lazily and
/// kept alive until the project is closed.
/// </summary>
public sealed partial class ProjectWorkspaceViewModel : ViewModelBase, INavigationAware, IRefreshable, IDisposable
{
    private readonly WorkspaceServices _services;
    private readonly ILogger _logger;
    private IWorkspaceSectionViewModel? _activeSection;
    private Task? _initialization;
    private bool _isActive;
    private bool _suppressTabActivation;
    private bool _disposed;

    public ProjectWorkspaceViewModel(ProjectContext context, WorkspaceServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        Context = context;
        _services = services;
        _logger = services.LoggerFactory.CreateLogger<ProjectWorkspaceViewModel>();

        Sync = new GitSyncViewModel(context, services, section => SelectSectionAsync(section));
        Branches = new BranchSelectorViewModel(context, services);
        Tabs = new ObservableCollection<WorkspaceTabViewModel>(
            WorkspaceSectionInfo.Ordered.Select(info => new WorkspaceTabViewModel(info, services.Sections.IsAvailable(info.Section))));
        var position = 0;
        foreach (var tab in Tabs.Where(t => t.IsAvailable))
        {
            tab.Shortcut = WorkspaceSectionInfo.ShortcutForPosition(position++);
        }

        Context.PropertyChanged += OnContextPropertyChanged;
        Context.GitStatusChanged += OnGitStatusChanged;
        Context.NavigationRequested += OnNavigationRequested;
        services.WorkItems.Changed += OnWorkItemsChanged;
        services.Status.SnapshotUpdated += OnSnapshotUpdated;
        services.Runs.RunStarted += OnRunStarted;
        services.Runs.RunCompleted += OnRunCompleted;

        UpdateIdentity();
        UpdateGitState();
        RebuildQuickCommands();
        UpdateRunningBadge();
    }

    public ProjectContext Context { get; }

    public string ProjectId => Context.ProjectId;

    public GitSyncViewModel Sync { get; }

    public BranchSelectorViewModel Branches { get; }

    /// <summary>Every section in tab order; unavailable ones (not registered by a feature) are hidden.</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    public IEnumerable<WorkspaceTabViewModel> AvailableTabs => Tabs.Where(t => t.IsAvailable);

    public bool HasAvailableSections => Tabs.Any(t => t.IsAvailable);

    /// <summary>
    /// Tabs whose section was created, in opening order. The view keeps one live view per entry
    /// (only the current one visible) so terminals, scroll positions and selections survive tab switches.
    /// </summary>
    public ObservableCollection<WorkspaceTabViewModel> OpenedTabs { get; } = [];

    public ObservableCollection<QuickCommandViewModel> QuickCommands { get; } = [];

    // ----- Identity ---------------------------------------------------------------------

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingFolderMessage))]
    public partial string Path { get; private set; } = string.Empty;

    public string MissingFolderMessage =>
        $"{Path} no longer exists. It may have been moved, renamed or deleted, or its drive is disconnected.";

    /// <summary>#RRGGBB avatar color chosen for the project, or null for the derived one.</summary>
    [ObservableProperty]
    public partial string? AvatarColor { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitHub))]
    public partial GitHubRepoRef? GitHub { get; private set; }

    public bool HasGitHub => GitHub is not null;

    public string? GitHubUrl => GitHub?.HtmlUrl;

    /// <summary>Name of the detected code editor ("Visual Studio Code"), null when none was found.</summary>
    public string? EditorName => _services.Shell.EditorName;

    public bool HasEditor => EditorName is not null;

    // ----- Git state --------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsRepositoryChecked { get; private set; }

    [ObservableProperty]
    public partial bool IsGitRepository { get; private set; }

    /// <summary>The project folder no longer exists (moved, renamed, deleted, drive disconnected).</summary>
    [ObservableProperty]
    public partial bool IsFolderMissing { get; private set; }

    /// <summary>Why git status could not be read (git missing, repository locked…); not set for a missing folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitError))]
    public partial ErrorInfo? GitError { get; private set; }

    public bool HasGitError => GitError is not null;

    /// <summary>Branch shown in the header and status bar ("main", "a1b2c3d (detached)").</summary>
    [ObservableProperty]
    public partial string? BranchName { get; private set; }

    [ObservableProperty]
    public partial bool IsDetached { get; private set; }

    [ObservableProperty]
    public partial int Ahead { get; private set; }

    [ObservableProperty]
    public partial int Behind { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangesText))]
    public partial int ChangedCount { get; private set; }

    /// <summary>"3 changes" for the status bar.</summary>
    public string ChangesText => Format.Count(ChangedCount, "change");

    [ObservableProperty]
    public partial int ConflictCount { get; private set; }

    // ----- Sections ---------------------------------------------------------------------

    [ObservableProperty]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    /// <summary>The view model of the selected tab, rendered by its DataTemplate.</summary>
    [ObservableProperty]
    public partial IWorkspaceSectionViewModel? CurrentSection { get; private set; }

    /// <summary>A section failed to open or load; shown instead of its content, with a retry.</summary>
    [ObservableProperty]
    public partial ErrorInfo? SectionError { get; private set; }

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (_disposed)
        {
            return;
        }

        _isActive = true;
        _initialization ??= InitializeAsync();
        if (argument is WorkspaceNavigationRequest request)
        {
            await SelectSectionAsync(request.Section, request.Argument).ConfigureAwait(true);
        }
        else if (SelectedTab is { } selected)
        {
            await ShowTabAsync(selected, null).ConfigureAwait(true);
        }
        else if (AvailableTabs.FirstOrDefault() is { } first)
        {
            await SelectSectionAsync(first.Section).ConfigureAwait(true);
        }

        await _initialization.ConfigureAwait(true);
    }

    public void OnNavigatedFrom()
    {
        _isActive = false;
        Branches.IsOpen = false;
        DeactivateActiveSection();
    }

    /// <summary>Shows a tab (creating its view model on first use) and passes it an optional argument.</summary>
    public async Task SelectSectionAsync(WorkspaceSection section, object? argument = null)
    {
        var tab = Tabs.FirstOrDefault(t => t.Section == section && t.IsAvailable);
        if (tab is null || _disposed)
        {
            _logger.LogInformation("Workspace section {Section} is not available", section);
            return;
        }

        _suppressTabActivation = true;
        try
        {
            SelectedTab = tab;
        }
        finally
        {
            _suppressTabActivation = false;
        }

        await ShowTabAsync(tab, argument).ConfigureAwait(true);
    }

    /// <summary>Ctrl+1 … Ctrl+0: selects the n-th visible tab (0-based index).</summary>
    [RelayCommand]
    private Task SelectSectionByIndexAsync(int index)
    {
        var tab = AvailableTabs.ElementAtOrDefault(index);
        return tab is null ? Task.CompletedTask : SelectSectionAsync(tab.Section);
    }

    [RelayCommand]
    private Task RetrySectionAsync() => SelectedTab is { } tab ? ShowTabAsync(tab, null, forceActivation: true) : Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
            await Task.WhenAll(RefreshOpenTasksAsync(), RefreshCiBadgeAsync()).ConfigureAwait(true);
            if (Branches.IsOpen)
            {
                await Branches.LoadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
    }

    // ----- Header actions ---------------------------------------------------------------

    [RelayCommand]
    private void OpenFolder() => Try(() => _services.Shell.OpenFolder(Context.Root), "Could not open the folder");

    [RelayCommand]
    private void OpenInEditor() => Try(() => _services.Shell.OpenFolderInEditor(Context.Root), "Could not open the code editor");

    [RelayCommand]
    private void OpenTerminal() => Try(() => _services.Shell.OpenExternalTerminal(Context.Root), "Could not open a terminal");

    [RelayCommand]
    private void OpenGitHub()
    {
        if (GitHubUrl is { } url)
        {
            Try(() => _services.Shell.OpenUrl(url), "Could not open the browser");
        }
    }

    [RelayCommand]
    private void CopyPath()
    {
        Try(() =>
        {
            _services.Shell.CopyToClipboard(Context.Root);
            _services.Notifications.Show("Path copied", Context.Root, NotificationSeverity.Info);
        }, "Could not copy the path");
    }

    /// <summary>"Locate folder…" of the missing-folder banner.</summary>
    [RelayCommand]
    private async Task LocateFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _services.Dialogs.PickFolderAsync($"Locate the folder of {Name}", ExistingParent(Context.Root)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the folder picker"));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var project = await _services.Registry.RelocateAsync(ProjectId, folder, Context.Lifetime).ConfigureAwait(true);
            Context.Project = project;
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
            _ = Context.RefreshProfileAsync();
            _services.Notifications.Show($"Found {project.Name}", project.Path, NotificationSeverity.Success);
        }, errorTitle: "Could not use this folder", errorMode: ErrorMode.Toast, notifications: _services.Notifications).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RemoveProjectAsync() => _services.ProjectActions.RemoveAsync(ProjectId);

    [RelayCommand]
    private async Task RunQuickCommandAsync(QuickCommandViewModel? quick)
    {
        if (quick is null || quick.IsStarting)
        {
            return;
        }

        quick.IsStarting = true;
        try
        {
            var command = quick.Command;
            var directory = string.IsNullOrEmpty(command.WorkingDirectory)
                ? Context.Root
                : PathUtil.ResolveUnder(Context.Root, command.WorkingDirectory);
            await _services.Runs.StartAsync(new RunRequest
            {
                ProjectId = ProjectId,
                Label = command.Name,
                CommandLine = command.CommandLine,
                WorkingDirectory = directory,
                Category = command.Category,
                CommandId = command.Id,
            }, Context.Lifetime).ConfigureAwait(true);
            await SelectSectionAsync(WorkspaceSection.Commands).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, $"Could not start {quick.Command.Name}"));
        }
        finally
        {
            quick.IsStarting = false;
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
        DeactivateActiveSection();
        Context.PropertyChanged -= OnContextPropertyChanged;
        Context.GitStatusChanged -= OnGitStatusChanged;
        Context.NavigationRequested -= OnNavigationRequested;
        _services.WorkItems.Changed -= OnWorkItemsChanged;
        _services.Status.SnapshotUpdated -= OnSnapshotUpdated;
        _services.Runs.RunStarted -= OnRunStarted;
        _services.Runs.RunCompleted -= OnRunCompleted;
        Sync.Dispose();
        Branches.Dispose();
        foreach (var section in Tabs.Select(t => t.ViewModel).OfType<IDisposable>())
        {
            try
            {
                section.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A workspace section failed to dispose");
            }
        }

        Context.Dispose();
    }

    // ----- Initialization and sections ---------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            await Context.InitializeAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize project {ProjectId}", ProjectId);
        }

        await Task.WhenAll(RefreshOpenTasksAsync(), RefreshCiBadgeAsync()).ConfigureAwait(true);
        Sync.StartAutoFetch();
    }

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value)
    {
        if (!_suppressTabActivation && value is not null)
        {
            _ = ShowTabAsync(value, null);
        }
    }

    private async Task ShowTabAsync(WorkspaceTabViewModel tab, object? argument, bool forceActivation = false)
    {
        if (_disposed || !tab.IsAvailable)
        {
            return;
        }

        IWorkspaceSectionViewModel section;
        try
        {
            section = tab.ViewModel ??= _services.Sections.Create(tab.Section, Context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the {Section} section", tab.Section);
            DeactivateActiveSection();
            CurrentSection = null;
            MarkCurrent(null);
            SectionError = ErrorInfo.From(ex, $"Could not open {tab.Title}");
            return;
        }

        if (!OpenedTabs.Contains(tab))
        {
            OpenedTabs.Add(tab);
        }

        if (!ReferenceEquals(CurrentSection, section))
        {
            DeactivateActiveSection();
            CurrentSection = section;
        }

        MarkCurrent(tab);

        if (forceActivation || SectionError is not null)
        {
            DeactivateActiveSection();
        }

        SectionError = null;
        if (_isActive && !ReferenceEquals(_activeSection, section))
        {
            _activeSection = section;
            try
            {
                await section.ActivateAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex.IsCancellation())
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The {Section} section failed to load", tab.Section);
                if (ReferenceEquals(CurrentSection, section))
                {
                    SectionError = ErrorInfo.From(ex, $"Could not load {tab.Title}");
                }
            }
        }

        if (argument is not null && section is INavigationTarget target && ReferenceEquals(CurrentSection, section))
        {
            try
            {
                await target.NavigateToAsync(argument).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex.IsCancellation())
            {
                // The project was closed.
            }
            catch (Exception ex)
            {
                _services.Notifications.ShowError(ErrorInfo.From(ex, $"Could not show this in {tab.Title}"));
            }
        }
    }

    private void MarkCurrent(WorkspaceTabViewModel? current)
    {
        foreach (var tab in Tabs)
        {
            tab.IsCurrent = ReferenceEquals(tab, current);
        }
    }

    private void DeactivateActiveSection()
    {
        var active = _activeSection;
        _activeSection = null;
        if (active is null)
        {
            return;
        }

        try
        {
            active.Deactivate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {Section} section failed to deactivate", active.Section);
        }
    }

    private void OnNavigationRequested(object? sender, WorkspaceNavigationRequest request) =>
        _services.Dispatcher.Post(() => _ = SelectSectionAsync(request.Section, request.Argument));

    // ----- Header state -----------------------------------------------------------------

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectContext.Project):
                UpdateIdentity();
                break;
            case nameof(ProjectContext.Profile):
                RebuildQuickCommands();
                break;
        }
    }

    private void OnGitStatusChanged(object? sender, EventArgs e) => UpdateGitState();

    private void UpdateIdentity()
    {
        var project = Context.Project;
        Name = project.Name;
        Path = project.Path;
        AvatarColor = project.Color;
        GitHub = project.GitHub;
        OnPropertyChanged(nameof(GitHubUrl));
    }

    private void UpdateGitState()
    {
        var status = Context.GitStatus;
        IsRepositoryChecked = Context.IsRepositoryChecked;
        IsGitRepository = status is not null;
        IsFolderMissing = Context.IsRepositoryChecked && !Context.FolderExists;
        GitError = IsFolderMissing ? null : Context.GitError;
        IsDetached = status?.IsDetached == true;
        BranchName = BranchSelectorViewModel.DisplayName(status);
        Ahead = status?.Ahead ?? 0;
        Behind = status?.Behind ?? 0;
        ChangedCount = status?.Entries.Count ?? 0;
        ConflictCount = status?.Conflicted.Count() ?? 0;

        var gitTab = Tab(WorkspaceSection.Git);
        if (ConflictCount > 0)
        {
            gitTab.SetBadge(BadgeCount(ConflictCount), StatusTone.Danger, Format.Count(ConflictCount, "conflicted file"));
        }
        else if (ChangedCount > 0)
        {
            gitTab.SetBadge(BadgeCount(ChangedCount), StatusTone.Neutral, Format.Count(ChangedCount, "changed file"));
        }
        else
        {
            gitTab.ClearBadge();
        }
    }

    private void RebuildQuickCommands()
    {
        var existing = QuickCommands.ToDictionary(q => q.Command.Id, StringComparer.Ordinal);
        var desired = QuickCommandViewModel.Select(Context.Profile)
            .Select(command => existing.TryGetValue(command.Id, out var current) && current.Command == command ? current : new QuickCommandViewModel(command))
            .ToList();
        QuickCommands.SyncWith(desired);
    }

    // ----- Badges -----------------------------------------------------------------------

    private async Task RefreshOpenTasksAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var open = await _services.WorkItems.CountOpenAsync(ProjectId, Context.Lifetime).ConfigureAwait(true);
            var tab = Tab(WorkspaceSection.Tasks);
            if (open > 0)
            {
                tab.SetBadge(BadgeCount(open), StatusTone.Neutral, Format.Count(open, "open task"));
            }
            else
            {
                tab.ClearBadge();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not count open tasks for {ProjectId}", ProjectId);
        }
    }

    private async Task RefreshCiBadgeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var snapshot = await _services.Status.GetCachedAsync(ProjectId, Context.Lifetime).ConfigureAwait(true);
            ApplyCi(snapshot?.Ci);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the cached CI state of {ProjectId}", ProjectId);
        }
    }

    private void ApplyCi(CiSummary? ci)
    {
        var (tone, description) = ci?.State switch
        {
            CiState.Success => (StatusTone.Success, "CI passing"),
            CiState.Failure => (StatusTone.Danger, "CI failing"),
            CiState.Running => (StatusTone.Running, "CI running"),
            CiState.Queued => (StatusTone.Warning, "CI queued"),
            CiState.Cancelled => (StatusTone.Neutral, "CI cancelled"),
            _ => (StatusTone.None, null),
        };

        if (ci?.Branch is { } branch && description is not null)
        {
            description = $"{description} on {branch}";
        }

        Tab(WorkspaceSection.GitHub).SetBadge(null, tone, description);
    }

    private void UpdateRunningBadge()
    {
        if (_disposed)
        {
            return;
        }

        var running = _services.Runs.ActiveRuns.Count(r =>
            string.Equals(r.Request.ProjectId, ProjectId, StringComparison.Ordinal) && r.Status is RunStatus.Running or RunStatus.Queued);
        var tab = Tab(WorkspaceSection.Commands);
        if (running > 0)
        {
            tab.SetBadge(BadgeCount(running), StatusTone.Running, running == 1 ? "1 command running" : $"{running} commands running");
        }
        else
        {
            tab.ClearBadge();
        }
    }

    private void OnWorkItemsChanged(object? sender, WorkItemsChangedEventArgs e)
    {
        if (string.Equals(e.ProjectId, ProjectId, StringComparison.Ordinal))
        {
            _services.Dispatcher.Post(() => _ = RefreshOpenTasksAsync());
        }
    }

    private void OnSnapshotUpdated(object? sender, ProjectSnapshot snapshot)
    {
        if (string.Equals(snapshot.ProjectId, ProjectId, StringComparison.Ordinal))
        {
            _services.Dispatcher.Post(() =>
            {
                if (!_disposed)
                {
                    ApplyCi(snapshot.Ci);
                }
            });
        }
    }

    private void OnRunStarted(object? sender, IRunSession session) => _services.Dispatcher.Post(UpdateRunningBadge);

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e) => _services.Dispatcher.Post(UpdateRunningBadge);

    private WorkspaceTabViewModel Tab(WorkspaceSection section) => Tabs[(int)section];

    private static string BadgeCount(int count) => count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture);

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

    private static string? ExistingParent(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                directory = System.IO.Path.GetDirectoryName(directory);
            }

            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }
}
