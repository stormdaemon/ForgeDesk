using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>
/// The home page: every registered project as a card or a row, answering "what needs my attention
/// right now?". Cached statuses render first; the list then follows the registry, the status
/// service and running commands live. Search, filter chips, sort and layout are view state; sort and
/// layout are remembered in the settings.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase, IRefreshable, INavigationAware, IDisposable, IProjectCardHost
{
    public const int MaxGroupLength = 40;

    private readonly IProjectRegistry _registry;
    private readonly IProjectStatusService _status;
    private readonly IRunService _runs;
    private readonly IProjectActions _actions;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly IBackgroundOperations _operations;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dictionary<string, ProjectCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<ProjectCardViewModel, string> _shownGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Debouncer _reloadDebouncer = new(TimeSpan.FromMilliseconds(150));
    private readonly Debouncer _viewDebouncer = new(TimeSpan.FromMilliseconds(120));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _hasLoaded;
    private bool _applyingView;
    private bool _restoringPreferences;
    private string? _editorName;
    private bool _disposed;

    public DashboardViewModel(
        IProjectRegistry registry,
        IProjectStatusService status,
        IRunService runs,
        IProjectActions actions,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        IBackgroundOperations operations,
        IUiDispatcher dispatcher,
        ILogger<DashboardViewModel> logger)
    {
        _registry = registry;
        _status = status;
        _runs = runs;
        _actions = actions;
        _settings = settings;
        _dialogs = dialogs;
        _notifications = notifications;
        _shell = shell;
        _operations = operations;
        _dispatcher = dispatcher;
        _logger = logger;
        _lifetimeToken = _lifetime.Token;

        AllFilter = new DashboardFilterViewModel(DashboardFilterKind.All, "All", "Apps20");
        AttentionFilter = new DashboardFilterViewModel(DashboardFilterKind.Attention, "Needs attention", "Warning16");
        PinnedFilter = new DashboardFilterViewModel(DashboardFilterKind.Pinned, "Pinned", "Pin16");
        RunningFilter = new DashboardFilterViewModel(DashboardFilterKind.Running, "Running", "Play20");
        Filters = [AllFilter, AttentionFilter, PinnedFilter, RunningFilter];
        SelectedFilter = AllFilter;

        _restoringPreferences = true;
        SelectedSortOption = DashboardSortOption.For(DashboardPreferences.ParseSort(settings.Current.DashboardSort));
        Layout = DashboardPreferences.ParseLayout(settings.Current.DashboardLayout);
        _restoringPreferences = false;

        _registry.Changed += OnRegistryChanged;
        _status.SnapshotUpdated += OnSnapshotUpdated;
        _runs.RunStarted += OnRunStarted;
        _runs.RunCompleted += OnRunCompleted;
    }

    /// <summary>Delay that coalesces bursts of live updates (zero applies them immediately, for tests).</summary>
    internal TimeSpan LiveUpdateDelay { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>How many projects "Refresh all" refreshes at the same time.</summary>
    public int RefreshConcurrency { get; init; } = 3;

    /// <summary>The visible projects: filtered, searched and sorted.</summary>
    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];

    public ObservableCollection<DashboardFilterViewModel> Filters { get; }

    public DashboardFilterViewModel AllFilter { get; }

    public DashboardFilterViewModel AttentionFilter { get; }

    public DashboardFilterViewModel PinnedFilter { get; }

    public DashboardFilterViewModel RunningFilter { get; }

    public IReadOnlyList<DashboardSortOption> SortOptions => DashboardSortOption.All;

    [ObservableProperty]
    public partial DashboardFilterViewModel SelectedFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch), nameof(NoMatchesDescription))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    public partial DashboardSortOption SelectedSortOption { get; set; }

    public DashboardSortMode Sort => SelectedSortOption?.Mode ?? DashboardSortMode.Attention;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridLayout), nameof(IsListLayout), nameof(ShowGrid), nameof(ShowList))]
    public partial DashboardLayout Layout { get; set; }

    public bool IsGridLayout
    {
        get => Layout == DashboardLayout.Grid;
        set
        {
            if (value)
            {
                Layout = DashboardLayout.Grid;
            }
        }
    }

    public bool IsListLayout
    {
        get => Layout == DashboardLayout.List;
        set
        {
            if (value)
            {
                Layout = DashboardLayout.List;
            }
        }
    }

    /// <summary>The view shows group headers (sorted by name or recency, and groups exist).</summary>
    [ObservableProperty]
    public partial bool IsGrouped { get; private set; }

    [ObservableProperty]
    public partial ProjectCardViewModel? SelectedProject { get; set; }

    /// <summary>First load in progress: the view shows skeleton cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHero), nameof(ShowNoMatches), nameof(ShowProjects), nameof(ShowGrid), nameof(ShowList))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHero), nameof(ShowNoMatches), nameof(ShowProjects), nameof(ShowGrid), nameof(ShowList))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(HasProjects), nameof(ShowHero), nameof(ShowNoMatches), nameof(ShowProjects), nameof(ShowToolbar))]
    public partial int ProjectCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int AttentionCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int RunningCount { get; private set; }

    /// <summary>"Refresh all" is running.</summary>
    [ObservableProperty]
    public partial bool IsRefreshingAll { get; private set; }

    public bool HasProjects => ProjectCount > 0;

    /// <summary>"12 projects · 3 need attention · 1 running".</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>(3) { Format.Count(ProjectCount, "project") };
            if (AttentionCount > 0)
            {
                parts.Add(AttentionCount == 1 ? "1 needs attention" : $"{AttentionCount} need attention");
            }

            if (RunningCount > 0)
            {
                parts.Add($"{RunningCount} running");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>No project registered: the welcome hero replaces the list.</summary>
    public bool ShowHero => IsLoaded && !IsLoading && !HasError && ProjectCount == 0;

    public bool ShowToolbar => ProjectCount > 0;

    /// <summary>Projects exist but none matches the search and filter.</summary>
    public bool ShowNoMatches => IsLoaded && !HasError && ProjectCount > 0 && Projects.Count == 0;

    public bool ShowProjects => IsLoaded && !HasError && Projects.Count > 0;

    /// <summary>The projects are shown as cards.</summary>
    public bool ShowGrid => ShowProjects && IsGridLayout;

    /// <summary>The projects are shown as rows.</summary>
    public bool ShowList => ShowProjects && IsListLayout;

    public string NoMatchesDescription => HasSearch
        ? $"No project matches “{SearchText.Trim()}” with the current filter."
        : "No project matches this filter right now.";

    public bool CanClone => _actions.CanClone;

    string? IProjectCardHost.EditorName => _editorName;

    // ----- Loading ----------------------------------------------------------------------------

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (_disposed)
        {
            return;
        }

        if (!_hasLoaded)
        {
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            await ReloadQuietlyAsync().ConfigureAwait(true);
        }
    }

    public void OnNavigatedFrom()
    {
    }

    /// <summary>F5: re-reads the projects, then refreshes every status.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!await LoadAsync().ConfigureAwait(true))
        {
            return;
        }

        if (RefreshAllCommand.CanExecute(null))
        {
            await RefreshAllCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private async Task<bool> LoadAsync()
    {
        IsLoading = !_hasLoaded;
        try
        {
            var loaded = await RunAsync(ReloadCoreAsync, errorTitle: "Could not load your projects").ConfigureAwait(true);
            _hasLoaded |= loaded;
            return loaded;
        }
        finally
        {
            IsLoading = false;
            IsLoaded = true;
            NotifyStateChanged();
        }
    }

    /// <summary>Background reloads keep the current list on failure (the next one retries).</summary>
    private async Task ReloadQuietlyAsync()
    {
        try
        {
            await ReloadCoreAsync().ConfigureAwait(true);
            Error = null;
            NotifyStateChanged();
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not reload the project list");
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
    }

    private async Task ReloadCoreAsync()
    {
        var projects = await _registry.GetAllAsync(_lifetimeToken).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        _editorName = ReadEditorName();
        var added = new List<ProjectCardViewModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            ids.Add(project.Id);
            if (_cards.TryGetValue(project.Id, out var card))
            {
                card.Update(project);
                card.NotifyEditorChanged();
            }
            else
            {
                card = new ProjectCardViewModel(project, this);
                _cards.Add(project.Id, card);
                added.Add(card);
            }
        }

        foreach (var removed in _cards.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _shownGroups.Remove(_cards[removed]);
            _cards.Remove(removed);
        }

        var cached = await Task.WhenAll(added.Select(ReadCachedSnapshotAsync)).ConfigureAwait(true);
        foreach (var (card, snapshot) in added.Zip(cached))
        {
            if (snapshot is not null)
            {
                card.Apply(snapshot);
            }
        }

        UpdateRunning();
        ApplyView();
    }

    private async Task<ProjectSnapshot?> ReadCachedSnapshotAsync(ProjectCardViewModel card)
    {
        try
        {
            return await _status.GetCachedAsync(card.Id, _lifetimeToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "No cached status for {ProjectId}", card.Id);
            return null;
        }
    }

    private string? ReadEditorName()
    {
        try
        {
            return _shell.EditorName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect the code editor");
            return null;
        }
    }

    // ----- View (filter, search, sort, groups) ------------------------------------------------

    partial void OnSelectedFilterChanged(DashboardFilterViewModel oldValue, DashboardFilterViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        // Ctrl+click can deselect the chip: fall back to "All" rather than to no filter at all.
        if (newValue is null)
        {
            SelectedFilter = AllFilter;
            return;
        }

        newValue.IsActive = true;
        ApplyViewIfLoaded();
    }

    partial void OnSearchTextChanged(string value) => ApplyViewIfLoaded();

    partial void OnSelectedSortOptionChanged(DashboardSortOption value)
    {
        if (value is null)
        {
            SelectedSortOption = DashboardSortOption.For(DashboardSortMode.Attention);
            return;
        }

        OnPropertyChanged(nameof(Sort));
        ApplyViewIfLoaded();
        if (!_restoringPreferences)
        {
            _ = SavePreferenceAsync(s => s with { DashboardSort = DashboardPreferences.ToSetting(value.Mode) });
        }
    }

    partial void OnLayoutChanged(DashboardLayout value)
    {
        if (!_restoringPreferences)
        {
            _ = SavePreferenceAsync(s => s with { DashboardLayout = DashboardPreferences.ToSetting(value) });
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedFilter = AllFilter;
    }

    private void ApplyViewIfLoaded()
    {
        if (_hasLoaded || _cards.Count > 0)
        {
            ApplyView();
        }
    }

    private void ScheduleViewUpdate()
    {
        if (LiveUpdateDelay <= TimeSpan.Zero)
        {
            ApplyView();
        }
        else
        {
            _viewDebouncer.Trigger(() => _dispatcher.InvokeAsync(ApplyView));
        }
    }

    /// <summary>Recomputes the visible list, the counts and the filter chips.</summary>
    internal void ApplyView()
    {
        if (_disposed || _applyingView)
        {
            return;
        }

        _applyingView = true;
        try
        {
            ApplyViewCore();
        }
        finally
        {
            _applyingView = false;
        }
    }

    private void ApplyViewCore()
    {
        var all = _cards.Values.ToList();
        RebuildGroupFilters(all);
        var filter = SelectedFilter ?? AllFilter;
        var terms = DashboardOrdering.SearchTerms(SearchText);
        var visible = all.Where(filter.Matches).Where(c => c.Matches(terms)).ToList();
        var grouped = filter.Kind != DashboardFilterKind.Group && DashboardOrdering.ShouldGroup(Sort, all);
        var ordered = DashboardOrdering.Order(visible, Sort, grouped);

        // A card whose group changed must be re-inserted so the grouped view moves it under its new header.
        if (grouped)
        {
            foreach (var card in Projects.Where(c => _shownGroups.TryGetValue(c, out var shown) && shown != c.GroupHeader).ToList())
            {
                Projects.Remove(card);
            }
        }

        if (grouped != IsGrouped)
        {
            Projects.Clear();
        }

        IsGrouped = grouped;
        Projects.SyncWith(ordered);
        _shownGroups.Clear();
        foreach (var card in ordered)
        {
            _shownGroups[card] = card.GroupHeader;
        }

        if (SelectedProject is { } selected && !Projects.Contains(selected))
        {
            SelectedProject = null;
        }

        ProjectCount = all.Count;
        AttentionCount = all.Count(c => c.NeedsAttention);
        RunningCount = all.Count(c => c.IsRunning);
        AllFilter.Count = all.Count;
        AttentionFilter.Count = AttentionCount;
        PinnedFilter.Count = all.Count(c => c.IsPinned);
        RunningFilter.Count = RunningCount;
        foreach (var groupFilter in Filters.Where(f => f.Kind == DashboardFilterKind.Group))
        {
            groupFilter.Count = all.Count(groupFilter.Matches);
        }

        NotifyStateChanged();
    }

    private void RebuildGroupFilters(IReadOnlyList<ProjectCardViewModel> all)
    {
        var groups = all.Select(c => c.Group).OfType<string>()
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var existing = Filters.Where(f => f.Kind == DashboardFilterKind.Group)
            .ToDictionary(f => f.Group!, StringComparer.CurrentCultureIgnoreCase);
        var desired = new List<DashboardFilterViewModel> { AllFilter, AttentionFilter, PinnedFilter, RunningFilter };
        desired.AddRange(groups.Select(g => existing.TryGetValue(g, out var chip) ? chip : new DashboardFilterViewModel(DashboardFilterKind.Group, g, "Folder16", g)));

        var selected = SelectedFilter;
        Filters.SyncWith(desired);
        if (!Filters.Contains(selected))
        {
            SelectedFilter = AllFilter;
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(ShowHero));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowProjects));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowToolbar));
    }

    private async Task SavePreferenceAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not save the dashboard preferences");
        }
    }

    // ----- Actions ----------------------------------------------------------------------------

    [RelayCommand]
    private Task AddLocalProjectAsync() => _actions.AddLocalProjectAsync();

    [RelayCommand(CanExecute = nameof(CanClone))]
    private Task CloneRepositoryAsync() => _actions.CloneRepositoryAsync();

    /// <summary>Refreshes the status of every project, <see cref="RefreshConcurrency"/> at a time, as a background operation.</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        var projects = _cards.Values.Select(c => c.Project).ToList();
        if (projects.Count == 0 || _disposed)
        {
            return;
        }

        IsRefreshingAll = true;
        var total = projects.Count;
        var done = 0;
        var failed = 0;
        using var operation = _operations.Begin("Refreshing projects");
        operation.Progress = 0;
        operation.Message = $"0 of {total}";
        using var gate = new SemaphoreSlim(Math.Max(1, RefreshConcurrency));
        var token = _lifetimeToken;
        try
        {
            await Task.WhenAll(projects.Select(async project =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var snapshot = await _status.RefreshAsync(project, includeRemote: true, token).ConfigureAwait(false);

                    // Cards update as results arrive; the order and counts are recomputed once at the end.
                    _dispatcher.Post(() => ApplySnapshotToCard(snapshot));
                }
                catch (Exception ex) when (!ex.IsCancellation())
                {
                    // One unreachable project (network drive, locked repository) must not stop the others.
                    Interlocked.Increment(ref failed);
                    _logger.LogDebug(ex, "Could not refresh the status of {ProjectId}", project.Id);
                }
                finally
                {
                    gate.Release();
                    var finished = Interlocked.Increment(ref done);
                    _dispatcher.Post(() =>
                    {
                        operation.Progress = (double)finished / total;
                        operation.Message = $"{finished} of {total}";
                    });
                }
            })).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            return;
        }
        finally
        {
            IsRefreshingAll = false;
        }

        ApplyView();
        if (failed > 0)
        {
            _notifications.Show(
                failed == 1 ? "1 project could not be refreshed" : $"{failed} projects could not be refreshed",
                "Their folders may be unavailable (network drive, locked repository). Their last known status is shown.",
                NotificationSeverity.Warning);
        }
    }

    /// <summary>Adds folders dropped from Explorer (files and already registered folders are skipped).</summary>
    [RelayCommand]
    private async Task AddFoldersAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        var added = new List<Project>();
        var alreadyRegistered = 0;
        var notFolders = 0;
        ErrorInfo? firstError = null;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path))
            {
                notFolders++;
                continue;
            }

            try
            {
                if (await _registry.FindByPathAsync(path).ConfigureAwait(true) is not null)
                {
                    alreadyRegistered++;
                    continue;
                }

                added.Add(await _registry.AddAsync(path).ConfigureAwait(true));
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.AlreadyExists)
            {
                alreadyRegistered++;
            }
            catch (Exception ex) when (!ex.IsCancellation())
            {
                firstError ??= ErrorInfo.From(ex, $"Could not add {Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}");
            }
        }

        ReportDrop(added, alreadyRegistered, notFolders, firstError);
    }

    private void ReportDrop(IReadOnlyList<Project> added, int alreadyRegistered, int notFolders, ErrorInfo? error)
    {
        var notes = new List<string>(2);
        if (alreadyRegistered > 0)
        {
            notes.Add(alreadyRegistered == 1 ? "1 folder was already in ForgeDesk." : $"{alreadyRegistered} folders were already in ForgeDesk.");
        }

        if (notFolders > 0)
        {
            notes.Add(notFolders == 1 ? "1 item was skipped: only folders can be added." : $"{notFolders} items were skipped: only folders can be added.");
        }

        var detail = notes.Count == 0 ? null : string.Join(' ', notes);
        if (added.Count == 1)
        {
            var project = added[0];
            _notifications.Show($"Added {project.Name}", detail ?? project.Path, NotificationSeverity.Success,
                new NotificationAction("Open", () => _actions.OpenAsync(project.Id)));
        }
        else if (added.Count > 1)
        {
            _notifications.Show($"Added {added.Count} projects", detail, NotificationSeverity.Success);
        }
        else if (error is null && detail is not null)
        {
            _notifications.Show("Nothing new to add", detail, NotificationSeverity.Info);
        }

        if (error is not null)
        {
            _notifications.ShowError(error);
        }
    }

    // ----- Card actions (IProjectCardHost) ----------------------------------------------------

    Task IProjectCardHost.OpenAsync(ProjectCardViewModel card, WorkspaceSection? section) => _actions.OpenAsync(card.Id, section);

    void IProjectCardHost.OpenInExplorer(ProjectCardViewModel card) => _actions.OpenInExplorer(card.Path);

    void IProjectCardHost.OpenInEditor(ProjectCardViewModel card) =>
        Try(() => _shell.OpenFolderInEditor(card.Path), "Could not open the editor");

    void IProjectCardHost.OpenTerminal(ProjectCardViewModel card) =>
        Try(() => _shell.OpenExternalTerminal(card.Path), "Could not open a terminal");

    void IProjectCardHost.CopyPath(ProjectCardViewModel card) =>
        Try(() => _shell.CopyToClipboard(card.Path), "Could not copy the path");

    Task IProjectCardHost.TogglePinAsync(ProjectCardViewModel card) => _actions.TogglePinAsync(card.Id);

    Task IProjectCardHost.RenameAsync(ProjectCardViewModel card) => _actions.RenameAsync(card.Id);

    Task IProjectCardHost.RemoveAsync(ProjectCardViewModel card) => _actions.RemoveAsync(card.Id);

    /// <summary>Asks for a group name (existing groups are suggested); an empty name removes the project from its group.</summary>
    async Task IProjectCardHost.SetGroupAsync(ProjectCardViewModel card)
    {
        var groups = _cards.Values.Select(c => c.Group).OfType<string>()
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var message = groups.Count == 0
            ? "Groups gather related projects under one header and one filter. Leave the name empty to remove the project from its group."
            : $"Existing groups: {string.Join(", ", groups)}. Leave the name empty to remove the project from its group.";

        string? answer;
        try
        {
            answer = await _dialogs.PromptAsync(new PromptOptions
            {
                Title = $"Set the group of {card.Name}",
                Message = message,
                InitialValue = card.Group ?? string.Empty,
                Placeholder = "Group name, e.g. Work",
                ConfirmText = "Save",
                Validate = ValidateGroup,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not change the group"));
            return;
        }

        if (answer is null)
        {
            return;
        }

        var trimmed = answer.Trim();
        // Reuse the spelling of an existing group ("work" → "Work") so chips do not split.
        var group = trimmed.Length == 0 ? null : groups.FirstOrDefault(g => string.Equals(g, trimmed, StringComparison.CurrentCultureIgnoreCase)) ?? trimmed;
        if (string.Equals(group, card.Group, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var project = await _registry.GetAsync(card.Id).ConfigureAwait(true)
                ?? throw new ForgeException(ErrorKind.NotFound, "This project is no longer registered in ForgeDesk.");
            var updated = await _registry.UpdateAsync(project with { Group = group }).ConfigureAwait(true);
            card.Update(updated);
            ApplyView();
        }, errorTitle: "Could not change the group", errorMode: ErrorMode.Toast, notifications: _notifications).ConfigureAwait(true);
    }

    /// <summary>Points a project whose folder moved to its new location, then refreshes its status.</summary>
    async Task IProjectCardHost.LocateAsync(ProjectCardViewModel card)
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync($"Locate the folder of {card.Name}", ExistingParent(card.Path)).ConfigureAwait(true);
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
            var project = await _registry.RelocateAsync(card.Id, folder, _lifetimeToken).ConfigureAwait(true);
            card.Update(project);
            _notifications.Show($"Found {project.Name}", project.Path, NotificationSeverity.Success);
            var snapshot = await _status.RefreshAsync(project, includeRemote: false, _lifetimeToken).ConfigureAwait(true);
            ApplySnapshot(snapshot);
        }, errorTitle: "Could not use this folder", errorMode: ErrorMode.Toast, notifications: _notifications).ConfigureAwait(true);
    }

    internal static string? ValidateGroup(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length > MaxGroupLength ? $"Use at most {MaxGroupLength} characters." : null;
    }

    private void Try(Action action, string errorTitle)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, errorTitle));
        }
    }

    private static string? ExistingParent(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            while (directory is not null && !directory.Exists)
            {
                directory = directory.Parent;
            }

            return directory?.FullName;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // ----- Live updates -----------------------------------------------------------------------

    internal void ApplySnapshot(ProjectSnapshot snapshot)
    {
        if (ApplySnapshotToCard(snapshot))
        {
            ScheduleViewUpdate();
        }
    }

    private bool ApplySnapshotToCard(ProjectSnapshot? snapshot) =>
        snapshot is not null && !_disposed && _cards.TryGetValue(snapshot.ProjectId, out var card) && card.Apply(snapshot);

    private void UpdateRunning()
    {
        IReadOnlyList<IRunSession> active;
        try
        {
            active = _runs.ActiveRuns;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the running commands");
            return;
        }

        var running = active
            .Where(r => r.Status is RunStatus.Running or RunStatus.Queued)
            .Select(r => r.Request.ProjectId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var card in _cards.Values)
        {
            card.IsRunning = running.Contains(card.Id);
        }
    }

    private void OnRegistryChanged(object? sender, ProjectsChangedEventArgs e)
    {
        if (!_hasLoaded || _disposed)
        {
            return;
        }

        if (LiveUpdateDelay <= TimeSpan.Zero)
        {
            _dispatcher.Post(() => _ = ReloadQuietlyAsync());
        }
        else
        {
            _reloadDebouncer.Trigger(() => _dispatcher.InvokeAsync(() => _ = ReloadQuietlyAsync()));
        }
    }

    private void OnSnapshotUpdated(object? sender, ProjectSnapshot snapshot) => _dispatcher.Post(() => ApplySnapshot(snapshot));

    private void OnRunStarted(object? sender, IRunSession session) => _dispatcher.Post(OnRunsChanged);

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e) => _dispatcher.Post(OnRunsChanged);

    private void OnRunsChanged()
    {
        if (_disposed)
        {
            return;
        }

        UpdateRunning();
        ScheduleViewUpdate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Changed -= OnRegistryChanged;
        _status.SnapshotUpdated -= OnSnapshotUpdated;
        _runs.RunStarted -= OnRunStarted;
        _runs.RunCompleted -= OnRunCompleted;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _reloadDebouncer.Dispose();
        _viewDebouncer.Dispose();
    }
}
