using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Activity;

public sealed record ActivityTimelineOptions
{
    /// <summary>Only entries of this project; null for every project (global page).</summary>
    public string? ProjectId { get; init; }

    /// <summary>Global page: entries show their project, and entries without a reference open it.</summary>
    public bool IsGlobal { get; init; }

    public int PageSize { get; init; } = 100;

    /// <summary>Debounce of the search box; zero applies every keystroke immediately (tests).</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The activity journal as a timeline grouped by day, with group/outcome filters, debounced search,
/// keyset paging and live insertion of new entries. Shared by the project Activity tab and the
/// global Activity page; the owner decides how a reference is opened.
/// </summary>
public sealed partial class ActivityTimelineViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly IActivityLog _log;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ActivityTimelineOptions _options;
    private readonly Func<ActivityTarget, Task> _openTarget;
    private readonly Debouncer _searchDebouncer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ActivityEntry> _entries = [];
    private readonly Dictionary<long, ActivityItemViewModel> _items = [];
    private readonly Dictionary<DateOnly, ActivityDayHeaderViewModel> _headers = [];
    private Func<string, ActivityProjectInfo?>? _projectLookup;
    private CancellationTokenSource? _loadCts;
    private int _generation;
    private bool _suppressReload;
    private bool _disposed;

    public ActivityTimelineViewModel(
        IActivityLog log,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        IUiDispatcher dispatcher,
        ActivityTimelineOptions options,
        Func<ActivityTarget, Task> openTarget)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openTarget);
        _log = log;
        _dialogs = dialogs;
        _notifications = notifications;
        _shell = shell;
        _dispatcher = dispatcher;
        _options = options;
        _openTarget = openTarget;
        _searchDebouncer = new Debouncer(options.SearchDelay);

        Filters = ActivityGroups.Filters.Select(g => new ActivityFilterViewModel(g)).ToArray();
        _suppressReload = true;
        SelectedFilter = Filters[0];
        _suppressReload = false;

        _log.EntryAdded += OnEntryAdded;
    }

    public string? ProjectId => _options.ProjectId;

    public bool IsGlobal => _options.IsGlobal;

    /// <summary>Day headers and entries, newest first (one flat, virtualized list).</summary>
    public ObservableCollection<ActivityRowViewModel> Rows { get; } = [];

    public IReadOnlyList<ActivityFilterViewModel> Filters { get; }

    /// <summary>Number of entries currently loaded.</summary>
    public int LoadedCount => _entries.Count;

    [ObservableProperty]
    public partial ActivityFilterViewModel? SelectedFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    public partial bool FailuresOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ActivityRowViewModel? SelectedItem { get; set; }

    /// <summary>First page (or a filter change) loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    public partial bool HasLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    public partial bool HasEntries { get; private set; }

    public bool HasFilters => SelectedFilter?.Group is not (null or ActivityGroup.All) || FailuresOnly || !string.IsNullOrWhiteSpace(SearchText);

    public bool ShowSkeleton => IsLoading && !HasEntries;

    /// <summary>"Nothing happened yet": the journal is empty for this scope.</summary>
    public bool ShowEmpty => HasLoaded && !HasEntries && !HasFilters && !HasError;

    /// <summary>Filters hide every entry.</summary>
    public bool ShowNoMatches => HasLoaded && !HasEntries && HasFilters && !HasError;

    public string EmptyDescription => IsGlobal
        ? "ForgeDesk records what you do through it: commits, pushes and pulls, command runs, releases, tasks, GitHub actions and project changes. They will show up here."
        : "ForgeDesk records what you do in this project: commits, pushes and pulls, command runs, releases, tasks and GitHub actions. They will show up here.";

    /// <summary>Global page: provides project names and colors (null result = unknown project).</summary>
    public void SetProjectLookup(Func<string, ActivityProjectInfo?>? lookup)
    {
        _projectLookup = lookup;
        foreach (var item in _items.Values)
        {
            ApplyProject(item);
        }
    }

    /// <summary>Loads the first page with the current filters (replacing what is shown).</summary>
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_generation;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCts = cts;
        IsLoading = true;
        try
        {
            await RunAsync(async () =>
            {
                var page = await _log.QueryAsync(BuildQuery(null, null), cts.Token).ConfigureAwait(true);
                if (generation != _generation)
                {
                    return;
                }

                _entries.Clear();
                _entries.AddRange(page.DistinctBy(e => e.Id));
                HasMore = page.Count >= _options.PageSize;
                Rebuild();
                HasLoaded = true;
            }, errorTitle: "Could not load the activity").ConfigureAwait(true);
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(ShowNoMatches));
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Loads the next (older) page after the last loaded entry (keyset paging).</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_disposed || !HasMore || IsLoadingMore || IsLoading || _entries.Count == 0)
        {
            return;
        }

        var generation = _generation;
        var last = _entries[^1];
        IsLoadingMore = true;
        try
        {
            var page = await _log.QueryAsync(BuildQuery(last.At, last.Id), _loadCts?.Token ?? _lifetime.Token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            var known = _entries.Select(e => e.Id).ToHashSet();
            _entries.AddRange(page.Where(e => known.Add(e.Id)));
            HasMore = page.Count >= _options.PageSize;
            Rebuild();
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not load older activity"));
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task OpenEntryAsync(ActivityRowViewModel? row)
    {
        if (row is not ActivityItemViewModel { Target: { } target })
        {
            return;
        }

        try
        {
            if (target.Kind == ActivityTargetKind.Url && target.Url is { } url)
            {
                _shell.OpenUrl(url);
                return;
            }

            await _openTarget(target).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open this entry"));
        }
    }

    [RelayCommand]
    private void CopyEntry(ActivityRowViewModel? row)
    {
        if (row is not ActivityItemViewModel item)
        {
            return;
        }

        try
        {
            _shell.CopyToClipboard(item.CopyText);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not copy the entry"));
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressReload = true;
        try
        {
            SelectedFilter = Filters[0];
            FailuresOnly = false;
            SearchText = string.Empty;
        }
        finally
        {
            _suppressReload = false;
        }

        _ = LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasEntries))]
    private async Task ClearHistoryAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = IsGlobal ? "Clear all activity?" : "Clear this project's activity?",
            Message = IsGlobal
                ? "Every entry of every project is deleted from the journal. Your projects, commits, runs and tasks are not affected."
                : "Every entry of this project is deleted from the journal. Commits, runs and tasks are not affected.",
            ConfirmText = "Clear history",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _log.ClearAsync(ProjectId, _lifetime.Token).ConfigureAwait(true);
            _generation++;
            _entries.Clear();
            HasMore = false;
            Rebuild();
            _notifications.Show("Activity history cleared", null, NotificationSeverity.Success);
        }, errorTitle: "Could not clear the activity", errorMode: ErrorMode.Toast, notifications: _notifications).ConfigureAwait(true);
    }

    partial void OnSelectedFilterChanged(ActivityFilterViewModel? value)
    {
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
        ReloadForFilters();
    }

    partial void OnFailuresOnlyChanged(bool value) => ReloadForFilters();

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressReload || !HasLoaded && !IsLoading)
        {
            return;
        }

        if (_options.SearchDelay <= TimeSpan.Zero)
        {
            _ = LoadAsync();
        }
        else
        {
            _searchDebouncer.Trigger(() => _dispatcher.InvokeAsync<Task>(LoadAsync).Unwrap());
        }
    }

    private void ReloadForFilters()
    {
        if (!_suppressReload && (HasLoaded || IsLoading))
        {
            _ = LoadAsync();
        }
    }

    private ActivityQuery BuildQuery(DateTimeOffset? before, long? beforeId) => new()
    {
        ProjectId = ProjectId,
        Kinds = SelectedFilter is { } filter ? ActivityGroups.KindsOf(filter.Group) : null,
        Outcome = FailuresOnly ? ActivityOutcome.Failure : null,
        Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        Before = before,
        BeforeId = before is null ? null : beforeId,
        Limit = _options.PageSize,
    };

    /// <summary>True when a new entry belongs in the list with the current scope and filters.</summary>
    public bool Matches(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ProjectId is not null && !string.Equals(entry.ProjectId, ProjectId, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedFilter is { Group: not ActivityGroup.All } filter && ActivityGroups.Of(entry.Kind) != filter.Group)
        {
            return false;
        }

        if (FailuresOnly && entry.Outcome != ActivityOutcome.Failure)
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0
            || entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Error))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowEmpty)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowNoMatches)));
        }
    }

    private void OnEntryAdded(object? sender, ActivityEntry entry) => _dispatcher.Post(() => Insert(entry));

    /// <summary>Live insertion of a recorded entry, at its place in the (at, id) descending order.</summary>
    internal void Insert(ActivityEntry entry)
    {
        if (_disposed || !HasLoaded || !Matches(entry) || _entries.Any(e => e.Id == entry.Id))
        {
            return;
        }

        var index = _entries.FindIndex(e => IsNewer(entry, e));
        if (index < 0)
        {
            if (HasMore)
            {
                // Older than everything loaded: it will come with the next page.
                return;
            }

            index = _entries.Count;
        }

        _entries.Insert(index, entry);
        Rebuild();
    }

    private static bool IsNewer(ActivityEntry a, ActivityEntry b) => a.At > b.At || (a.At == b.At && a.Id > b.Id);

    private void Rebuild()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_options.Time.GetUtcNow(), _options.Time.LocalTimeZone).DateTime);
        var desired = new List<ActivityRowViewModel>(_entries.Count + 16);
        var usedItems = new HashSet<long>();
        var usedDays = new HashSet<DateOnly>();
        ActivityDayHeaderViewModel? header = null;
        var headerCount = 0;

        foreach (var entry in _entries)
        {
            if (!_items.TryGetValue(entry.Id, out var item))
            {
                item = new ActivityItemViewModel(entry, ActivityTarget.Resolve(entry, IsGlobal), _options.Time.LocalTimeZone) { ShowProject = IsGlobal };
                ApplyProject(item);
                _items[entry.Id] = item;
            }

            if (header is null || header.Day != item.Day)
            {
                if (header is not null)
                {
                    header.Count = headerCount;
                }

                if (!_headers.TryGetValue(item.Day, out header))
                {
                    header = new ActivityDayHeaderViewModel(item.Day);
                    _headers[item.Day] = header;
                }

                header.Label = ActivityDayHeaderViewModel.LabelFor(item.Day, today);
                usedDays.Add(item.Day);
                headerCount = 0;
                desired.Add(header);
            }

            headerCount++;
            usedItems.Add(entry.Id);
            desired.Add(item);
        }

        if (header is not null)
        {
            header.Count = headerCount;
        }

        foreach (var stale in _items.Keys.Where(id => !usedItems.Contains(id)).ToList())
        {
            _items.Remove(stale);
        }

        foreach (var stale in _headers.Keys.Where(day => !usedDays.Contains(day)).ToList())
        {
            _headers.Remove(stale);
        }

        Rows.SyncWith(desired);
        if (SelectedItem is ActivityItemViewModel selected && !usedItems.Contains(selected.Id))
        {
            SelectedItem = null;
        }

        HasEntries = _entries.Count > 0;
        OnPropertyChanged(nameof(LoadedCount));
    }

    private void ApplyProject(ActivityItemViewModel item)
    {
        if (!IsGlobal || item.ProjectId is not { } projectId)
        {
            return;
        }

        var info = _projectLookup?.Invoke(projectId);
        item.ProjectName = info?.Name;
        item.ProjectColor = info?.Color;

        // Entries of projects removed from ForgeDesk keep their text but no longer lead anywhere.
        var target = ActivityTarget.Resolve(item.Entry, isGlobal: true);
        item.Target = target is { Kind: not ActivityTargetKind.Url } && _projectLookup is not null && info is null ? null : target;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log.EntryAdded -= OnEntryAdded;
        _searchDebouncer.Dispose();
        _lifetime.Cancel();
        _loadCts?.Dispose();
        _lifetime.Dispose();
    }
}
