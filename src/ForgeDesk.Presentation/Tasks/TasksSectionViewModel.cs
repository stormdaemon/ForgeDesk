using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Tasks;

public enum TasksViewMode
{
    Board,
    List,
}

/// <summary>Columns of the list view, for sorting.</summary>
public enum TaskSortColumn
{
    Key,
    Title,
    Status,
    Priority,
    Labels,
    Updated,
    Due,
}

/// <summary>
/// The Tasks tab: a five-column board (Backlog · To do · In progress · Review · Done) with drag and
/// drop and inline quick add, a sortable list for large backlogs, local filters, and a details
/// pane where every edit is saved automatically. "Start working" creates or switches to the
/// task's branch and moves it to In progress.
/// </summary>
public sealed partial class TasksSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    /// <summary>Done cards older than this are collapsed behind "Show n more".</summary>
    public static readonly TimeSpan RecentDoneWindow = TimeSpan.FromDays(14);

    private readonly ILogger _logger;
    private readonly Dictionary<string, TaskCardViewModel> _cards = new(StringComparer.Ordinal);
    private Task? _loading;
    private bool _reloading;
    private bool _reloadPending;
    private bool _stale;
    private bool _openingDetail;
    private bool _disposed;

    public TasksSectionViewModel(ProjectContext context, WorkspaceServices services, IFileIndex? fileIndex = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        Context = context;
        Services = services;
        FileIndex = fileIndex;
        _logger = services.LoggerFactory.CreateLogger<TasksSectionViewModel>();
        Columns = new ObservableCollection<TaskColumnViewModel>(Enum.GetValues<WorkItemStatus>().Select(s => new TaskColumnViewModel(this, s)));
        CurrentBranch = context.GitStatus?.Branch;

        services.WorkItems.Changed += OnWorkItemsChanged;
        context.GitStatusChanged += OnGitStatusChanged;
        context.PropertyChanged += OnContextPropertyChanged;
    }

    public WorkspaceSection Section => WorkspaceSection.Tasks;

    public ProjectContext Context { get; }

    internal WorkspaceServices Services { get; }

    /// <summary>File search for "Link file…" (null in minimal hosts).</summary>
    internal IFileIndex? FileIndex { get; }

    internal DateTimeOffset Now => Services.Time.GetLocalNow();

    /// <summary>Pause after typing before a title or description is saved (InfiniteTimeSpan: only on flush, for tests).</summary>
    internal TimeSpan TextSaveDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    public bool IsActive { get; private set; }

    public bool HasLoaded { get; private set; }

    /// <summary>Branch checked out now (cards linked to it are highlighted).</summary>
    public string? CurrentBranch { get; private set; }

    // ----- Board and list ---------------------------------------------------------------

    public ObservableCollection<TaskColumnViewModel> Columns { get; }

    /// <summary>Rows of the list view (filtered and sorted).</summary>
    public ObservableCollection<TaskCardViewModel> ListItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoardView), nameof(IsListView), nameof(ShowBoardSurface), nameof(ShowListSurface))]
    public partial TasksViewMode ViewMode { get; set; }

    /// <summary>Board shown (two-way with the Board / List toggle).</summary>
    public bool IsBoardView
    {
        get => ViewMode == TasksViewMode.Board;
        set
        {
            if (value)
            {
                ViewMode = TasksViewMode.Board;
            }
        }
    }

    /// <summary>List shown (two-way with the Board / List toggle).</summary>
    public bool IsListView
    {
        get => ViewMode == TasksViewMode.List;
        set
        {
            if (value)
            {
                ViewMode = TasksViewMode.List;
            }
        }
    }

    [ObservableProperty]
    public partial TaskSortColumn SortColumn { get; private set; } = TaskSortColumn.Updated;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyBoard), nameof(ShowNoMatches), nameof(ShowContent), nameof(TotalText), nameof(ShowBoardSurface),
        nameof(ShowListSurface))]
    public partial int TotalCount { get; private set; }

    /// <summary>"Create your first task" was pressed on an empty board: show the board and its quick-add box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyBoard), nameof(ShowBoardSurface))]
    public partial bool IsStartingFirstTask { get; private set; }

    public bool ShowBoardSurface => IsBoardView && (TotalCount > 0 || IsStartingFirstTask);

    public bool ShowListSurface => IsListView && TotalCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    public partial int VisibleCount { get; private set; }

    [ObservableProperty]
    public partial int OpenCount { get; private set; }

    public string TotalText => $"{Format.Count(OpenCount, "open task")} · {Format.Count(TotalCount, "task")} in total";

    /// <summary>No task at all: "Plan your work".</summary>
    public bool ShowEmptyBoard => HasLoaded && !IsLoading && Error is null && TotalCount == 0 && !IsStartingFirstTask;

    public bool ShowContent => TotalCount > 0;

    public bool ShowNoMatches => TotalCount > 0 && VisibleCount == 0;

    // ----- Filters ----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial string SearchText { get; set; } = string.Empty;

    public IReadOnlyList<TaskPriorityFilter> PriorityFilters => TaskPriorityFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial TaskPriorityFilter PriorityFilter { get; set; } = TaskPriorityFilter.Any;

    /// <summary>Labels used by the project's tasks ("Any label" first, as null).</summary>
    public ObservableCollection<string> AvailableLabels { get; } = [];

    [ObservableProperty]
    public partial bool HasAvailableLabels { get; private set; }

    /// <summary>Only tasks with this label (null: any).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters), nameof(LabelFilterText))]
    public partial string? LabelFilter { get; set; }

    public string LabelFilterText => LabelFilter ?? "Any label";

    [ObservableProperty]
    public partial bool ShowDone { get; set; } = true;

    /// <summary>Done tasks completed more than two weeks ago are listed too.</summary>
    [ObservableProperty]
    public partial bool ShowOlderDone { get; set; }

    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchText) || PriorityFilter.Priority is not null || LabelFilter is not null;

    // ----- Details ----------------------------------------------------------------------

    [ObservableProperty]
    public partial TaskCardViewModel? SelectedCard { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial TaskDetailViewModel? Detail { get; private set; }

    public bool IsDetailOpen => Detail is not null;

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = true;
        if (!HasLoaded)
        {
            await EnsureLoadedAsync().ConfigureAwait(true);
        }
        else if (_stale)
        {
            await ReloadAsync().ConfigureAwait(true);
        }
        else
        {
            RefreshDates();
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        if (Detail is { } detail)
        {
            _ = detail.FlushAsync();
        }
    }

    /// <summary>
    /// Deep link: a work item id (or <see cref="TaskNavigation"/>) selects the task and opens its
    /// details; <see cref="TaskNavigation.CreateTask"/> focuses the quick-add box of "To do".
    /// </summary>
    public async Task NavigateToAsync(object argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureLoadedAsync().ConfigureAwait(true);
        switch (argument)
        {
            case TaskNavigation { NewTask: true }:
                NewTask();
                break;
            case TaskNavigation { WorkItemId: { } id }:
                await OpenByIdAsync(id).ConfigureAwait(true);
                break;
            case string id when !string.IsNullOrWhiteSpace(id):
                await OpenByIdAsync(id).ConfigureAwait(true);
                break;
        }
    }

    // ----- Commands ---------------------------------------------------------------------

    /// <summary>Ctrl+N: quick add in "To do".</summary>
    [RelayCommand]
    private void NewTask()
    {
        ViewMode = TasksViewMode.Board;
        if (TotalCount == 0)
        {
            IsStartingFirstTask = true;
        }

        Columns[(int)WorkItemStatus.Todo].OpenQuickAdd();
    }

    [RelayCommand]
    private void ShowBoard() => ViewMode = TasksViewMode.Board;

    [RelayCommand]
    private void ShowList() => ViewMode = TasksViewMode.List;

    [RelayCommand]
    private void SortBy(TaskSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is TaskSortColumn.Updated or TaskSortColumn.Priority;
        }

        ApplyFilters();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        PriorityFilter = TaskPriorityFilter.Any;
        LabelFilter = null;
    }

    [RelayCommand]
    private void ClearLabelFilter() => LabelFilter = null;

    [RelayCommand]
    private void FilterByLabel(string? label) => LabelFilter = string.IsNullOrWhiteSpace(label) ? null : label;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenAsync(TaskCardViewModel? card) => card is null ? Task.CompletedTask : OpenDetailAsync(card);

    [RelayCommand]
    public async Task CloseDetailAsync()
    {
        if (Detail is not { } detail)
        {
            return;
        }

        await detail.FlushAsync().ConfigureAwait(true);
        detail.Card.IsOpen = false;
        detail.Dispose();
        if (ReferenceEquals(Detail, detail))
        {
            Detail = null;
        }
    }

    /// <summary>Drop of a dragged card (from the board's code-behind).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task MoveCardAsync(TaskDropRequest? request)
    {
        if (request is null)
        {
            return;
        }

        var column = Columns[(int)request.Status];
        var ids = column.Cards.Select(c => c.Id).ToList();
        var position = TaskBoardMath.Neighbours(ids, request.Card.Id, request.Index);
        if (position.IsUnchanged && request.Card.Status == request.Status)
        {
            return;
        }

        // Move on screen first, so the card doesn't jump back while the database is updated.
        var source = Columns[(int)request.Card.Status];
        source.Cards.Remove(request.Card);
        column.Cards.Insert(Math.Clamp(position.Index, 0, column.Cards.Count), request.Card);

        var moved = await RunAsync(async () =>
        {
            var item = await Services.WorkItems.MoveAsync(request.Card.Id, request.Status, position.AfterId, position.BeforeId, Context.Lifetime)
                .ConfigureAwait(true);
            ApplyItem(item);
        }, errorTitle: $"Could not move {request.Card.Key}", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);

        if (!moved)
        {
            ApplyFilters();
        }
    }

    /// <summary>"Move to ›" of the card context menu: to the top of the column.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MoveToBacklogAsync(TaskCardViewModel? card) => MoveToTopAsync(card, WorkItemStatus.Backlog);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MoveToTodoAsync(TaskCardViewModel? card) => MoveToTopAsync(card, WorkItemStatus.Todo);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MoveToInProgressAsync(TaskCardViewModel? card) => MoveToTopAsync(card, WorkItemStatus.InProgress);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MoveToReviewAsync(TaskCardViewModel? card) => MoveToTopAsync(card, WorkItemStatus.Review);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MoveToDoneAsync(TaskCardViewModel? card) => MoveToTopAsync(card, WorkItemStatus.Done);

    private Task MoveToTopAsync(TaskCardViewModel? card, WorkItemStatus status) =>
        card is null || card.Status == status ? Task.CompletedTask : MoveCardAsync(new TaskDropRequest(card, status, 0));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task StartWorkingOnAsync(TaskCardViewModel? card) => card is null ? Task.CompletedTask : StartWorkingAsync(card);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CompleteCardAsync(TaskCardViewModel? card) => card is null ? Task.CompletedTask : CompleteAsync(card);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DeleteCardAsync(TaskCardViewModel? card) => card is null ? Task.CompletedTask : DeleteAsync(card);

    [RelayCommand]
    private void CopyCardKey(TaskCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            Services.Shell.CopyToClipboard($"{card.Key} {card.Title}");
            Services.Notifications.Show("Copied", $"{card.Key} {card.Title}", NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!HasLoaded)
        {
            await EnsureLoadedAsync().ConfigureAwait(true);
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RetryAsync() => ReloadAsync();

    // ----- Operations (shared with the details pane) ------------------------------------

    /// <summary>Creates a task at the bottom of a column. Returns true on success.</summary>
    internal async Task<bool> CreateAsync(WorkItemStatus status, string title)
    {
        TaskCardViewModel? created = null;
        var ok = await RunAsync(async () =>
        {
            var item = await Services.WorkItems.CreateAsync(Context.ProjectId, new WorkItemDraft(title, Status: status), Context.Lifetime).ConfigureAwait(true);
            created = ApplyItem(item);
        }, errorTitle: "Could not create the task", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);

        if (ok && created is not null && !Columns[(int)status].Cards.Contains(created))
        {
            // Hidden by a filter: say where it went.
            Services.Notifications.Show($"Created {created.Key}", "It is hidden by the current filters.", NotificationSeverity.Info);
        }

        return ok;
    }

    /// <summary>Takes a task returned by the service: updates (or adds) its card, the board, the list and the details.</summary>
    internal TaskCardViewModel ApplyItem(WorkItem item)
    {
        if (_cards.TryGetValue(item.Id, out var card))
        {
            card.Update(item, Now, CurrentBranch);
        }
        else
        {
            card = new TaskCardViewModel(item, Now, CurrentBranch);
            _cards[item.Id] = card;
        }

        if (Detail is { } detail && ReferenceEquals(detail.Card, card))
        {
            detail.Apply(item);
        }

        UpdateCounts();
        ApplyFilters();
        return card;
    }

    /// <summary>
    /// "Start working": creates the branch task/&lt;n&gt;-&lt;slug&gt; (or reuses it), switches to it
    /// (offering to stash uncommitted changes), links it, and moves the task to In progress.
    /// </summary>
    internal async Task StartWorkingAsync(TaskCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.IsWorking)
        {
            return;
        }

        if (!Context.IsGitRepository || Context.GitStatus is not { } status)
        {
            Services.Notifications.Show("Not a Git repository",
                $"{card.Key} needs a Git repository to get its own branch. Initialize one in the Git tab, or just move the task to In progress.",
                NotificationSeverity.Warning);
            return;
        }

        var branch = TaskBoardMath.BranchName(card.Item);
        if (BranchNames.Validate(branch) is { } invalid)
        {
            Services.Notifications.ShowError(new ErrorInfo(ErrorKind.InvalidInput, $"Could not name a branch for {card.Key}", invalid, null));
            return;
        }

        card.IsWorking = true;
        try
        {
            await RunAsync(async () =>
            {
                var branches = await Services.Git.GetBranchesAsync(Context.Root, includeRemote: false, Context.Lifetime).ConfigureAwait(true);
                var existing = branches.FirstOrDefault(b => !b.IsRemote && string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase));
                var name = existing?.Name ?? branch;
                var onBranch = string.Equals(status.Branch, name, StringComparison.Ordinal);
                var switchBranch = !onBranch;
                var stash = false;

                if (switchBranch && !status.IsClean)
                {
                    var dialog = new DirtyTreeDialogViewModel(name, status.Entries.Count, existing is not null);
                    await Services.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true);
                    switch (dialog.Choice)
                    {
                        case DirtyTreeChoice.Cancel:
                            return;
                        case DirtyTreeChoice.StashAndSwitch:
                            stash = true;
                            break;
                        case DirtyTreeChoice.CreateWithoutSwitching:
                            switchBranch = false;
                            break;
                    }
                }

                if (stash)
                {
                    await Services.Git.StashAsync(Context.Root, $"ForgeDesk: before starting {card.Key}", includeUntracked: true, Context.Lifetime)
                        .ConfigureAwait(true);
                }

                if (existing is null)
                {
                    await Services.Git.CreateBranchAsync(Context.Root, name, startPoint: null, checkout: switchBranch, Context.Lifetime).ConfigureAwait(true);
                }
                else if (switchBranch)
                {
                    await Services.Git.CheckoutAsync(Context.Root, name, Context.Lifetime).ConfigureAwait(true);
                }

                var item = card.Item;
                if (!item.Links.Any(l => l.Kind == WorkItemLinkKind.Branch && string.Equals(l.Value, name, StringComparison.Ordinal)))
                {
                    item = await Services.WorkItems.AddLinkAsync(card.Id, WorkItemLinkKind.Branch, name, cancellationToken: Context.Lifetime).ConfigureAwait(true);
                    ApplyItem(item);
                }

                if (item.Status != WorkItemStatus.InProgress)
                {
                    var top = Columns[(int)WorkItemStatus.InProgress].Cards.FirstOrDefault(c => c.Id != card.Id)?.Id;
                    item = await Services.WorkItems.MoveAsync(card.Id, WorkItemStatus.InProgress, null, top, Context.Lifetime).ConfigureAwait(true);
                    ApplyItem(item);
                }

                await RecordActivityAsync(card, name, existing is null, switchBranch || onBranch).ConfigureAwait(true);
                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);

                var where = switchBranch || onBranch ? $"on branch {name}" : $"— branch {name} created; you're still on {status.Branch ?? "the current commit"}";
                Services.Notifications.Show($"Working on {card.Key}", $"{card.Title} {where}" + (stash ? ". Your changes were stashed." : "."),
                    NotificationSeverity.Success);
            }, errorTitle: $"Could not start working on {card.Key}", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
        }
        finally
        {
            card.IsWorking = false;
        }
    }

    /// <summary>"Complete": moves the task to the top of Done.</summary>
    internal async Task CompleteAsync(TaskCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.IsDone || card.IsWorking)
        {
            return;
        }

        card.IsWorking = true;
        try
        {
            await RunAsync(async () =>
            {
                var top = Columns[(int)WorkItemStatus.Done].Cards.FirstOrDefault()?.Id;
                var item = await Services.WorkItems.MoveAsync(card.Id, WorkItemStatus.Done, null, top, Context.Lifetime).ConfigureAwait(true);
                ApplyItem(item);
                Services.Notifications.Show($"Completed {card.Key}", card.Title, NotificationSeverity.Success);
            }, errorTitle: $"Could not complete {card.Key}", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
        }
        finally
        {
            card.IsWorking = false;
        }
    }

    internal async Task DeleteAsync(TaskCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        bool confirmed;
        try
        {
            confirmed = await Services.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = $"Delete {card.Key}?",
                Message = $"“{card.Title}” is deleted with its links and history. This can't be undone.",
                ConfirmText = "Delete task",
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not delete the task"));
            return;
        }

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Services.WorkItems.DeleteAsync(card.Id, Context.Lifetime).ConfigureAwait(true);
            if (Detail?.Card == card)
            {
                Detail.Dispose();
                Detail = null;
            }

            _cards.Remove(card.Id);
            UpdateCounts();
            ApplyFilters();
            Services.Notifications.Show($"Deleted {card.Key}", card.Title, NotificationSeverity.Success);
        }, errorTitle: $"Could not delete {card.Key}", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Services.WorkItems.Changed -= OnWorkItemsChanged;
        Context.GitStatusChanged -= OnGitStatusChanged;
        Context.PropertyChanged -= OnContextPropertyChanged;
        if (Detail is { } detail)
        {
            _ = detail.FlushAsync();
            detail.Dispose();
        }
    }

    // ----- Loading ----------------------------------------------------------------------

    private Task EnsureLoadedAsync() => HasLoaded ? Task.CompletedTask : _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
            _loading = null;
            OnPropertyChanged(nameof(ShowEmptyBoard));
        }
    }

    /// <summary>Re-reads every task (coalesced: one reload runs at a time, a second one follows if requested meanwhile).</summary>
    private async Task ReloadAsync()
    {
        if (_reloading)
        {
            _reloadPending = true;
            return;
        }

        _reloading = true;
        try
        {
            do
            {
                _reloadPending = false;
                await ReloadCoreAsync().ConfigureAwait(true);
            }
            while (_reloadPending && !_disposed);
        }
        finally
        {
            _reloading = false;
            OnPropertyChanged(nameof(ShowEmptyBoard));
        }
    }

    private async Task ReloadCoreAsync()
    {
        var ok = await RunAsync(async () =>
        {
            var items = await Services.WorkItems.GetAllAsync(Context.ProjectId, Context.Lifetime).ConfigureAwait(true);
            var now = Now;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                seen.Add(item.Id);
                if (_cards.TryGetValue(item.Id, out var card))
                {
                    card.Update(item, now, CurrentBranch);
                    if (Detail is { } detail && ReferenceEquals(detail.Card, card))
                    {
                        detail.Apply(item);
                    }
                }
                else
                {
                    _cards[item.Id] = new TaskCardViewModel(item, now, CurrentBranch);
                }
            }

            foreach (var gone in _cards.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                var card = _cards[gone];
                _cards.Remove(gone);
                if (Detail?.Card == card)
                {
                    Detail.Dispose();
                    Detail = null;
                }
            }

            _stale = false;
            UpdateCounts();
            ApplyFilters();
        }, "Loading tasks…", "Could not load the tasks").ConfigureAwait(true);

        if (ok)
        {
            HasLoaded = true;
        }
    }

    private async Task OpenByIdAsync(string id)
    {
        if (!_cards.TryGetValue(id, out var card))
        {
            await ReloadAsync().ConfigureAwait(true);
            if (!_cards.TryGetValue(id, out card))
            {
                Services.Notifications.Show("Task not found", "It may have been deleted.", NotificationSeverity.Warning);
                return;
            }
        }

        if (!IsVisible(card))
        {
            ClearFilters();
            if (card.IsDone)
            {
                ShowDone = true;
                ShowOlderDone = true;
            }
        }

        await OpenDetailAsync(card).ConfigureAwait(true);
    }

    private async Task OpenDetailAsync(TaskCardViewModel card)
    {
        _openingDetail = true;
        try
        {
            SelectedCard = card;
        }
        finally
        {
            _openingDetail = false;
        }

        if (Detail?.Card == card)
        {
            return;
        }

        var previous = Detail;
        var detail = new TaskDetailViewModel(this, card);
        Detail = detail;
        if (previous is not null)
        {
            previous.Card.IsOpen = false;
            await previous.FlushAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        card.IsOpen = true;
        await detail.LoadHistoryAsync().ConfigureAwait(true);
    }

    // ----- Filtering --------------------------------------------------------------------

    private bool PassesFilters(TaskCardViewModel card) =>
        card.Matches(SearchText)
        && (PriorityFilter.Priority is not { } priority || card.Priority == priority)
        && (LabelFilter is not { } label || card.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

    private bool IsRecentDone(TaskCardViewModel card) =>
        (card.Item.CompletedAt ?? card.Item.UpdatedAt) >= Now - RecentDoneWindow;

    private bool IsVisible(TaskCardViewModel card) =>
        PassesFilters(card) && (!card.IsDone || (ShowDone && (ShowOlderDone || IsRecentDone(card))));

    private void ApplyFilters()
    {
        var visible = 0;
        foreach (var column in Columns)
        {
            var matching = _cards.Values
                .Where(c => c.Status == column.Status && PassesFilters(c))
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Number)
                .ToList();
            var shown = matching;
            if (column.IsDoneColumn)
            {
                shown = !ShowDone ? [] : ShowOlderDone ? matching : matching.Where(IsRecentDone).ToList();
                column.HiddenCount = ShowDone ? matching.Count - shown.Count : 0;
                column.IsVisible = ShowDone;
            }

            column.Count = matching.Count;
            column.Cards.SyncWith(shown);
            visible += shown.Count + (column.IsDoneColumn ? column.HiddenCount : 0);
        }

        var rows = _cards.Values.Where(c => PassesFilters(c) && (!c.IsDone || ShowDone)).ToList();
        ListItems.SyncWith(Sort(rows));
        VisibleCount = IsListView ? rows.Count : visible;
        if (SelectedCard is not null && !_cards.ContainsKey(SelectedCard.Id))
        {
            SelectedCard = null;
        }
    }

    private List<TaskCardViewModel> Sort(List<TaskCardViewModel> rows)
    {
        IOrderedEnumerable<TaskCardViewModel> ordered = SortColumn switch
        {
            TaskSortColumn.Key => Order(rows, c => c.Number),
            TaskSortColumn.Title => Order(rows, c => c.Title, StringComparer.CurrentCultureIgnoreCase),
            TaskSortColumn.Status => Order(rows, c => (int)c.Status),
            TaskSortColumn.Priority => Order(rows, c => (int)c.Priority),
            TaskSortColumn.Labels => Order(rows, c => c.LabelsText, StringComparer.CurrentCultureIgnoreCase),
            TaskSortColumn.Due => SortDescending
                ? rows.OrderByDescending(c => c.DueAt ?? DateTimeOffset.MinValue)
                : rows.OrderBy(c => c.DueAt ?? DateTimeOffset.MaxValue),
            _ => Order(rows, c => c.UpdatedAt),
        };

        return ordered.ThenByDescending(c => c.Number).ToList();
    }

    private IOrderedEnumerable<TaskCardViewModel> Order<TKey>(IEnumerable<TaskCardViewModel> rows, Func<TaskCardViewModel, TKey> key, IComparer<TKey>? comparer = null) =>
        SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    private void UpdateCounts()
    {
        TotalCount = _cards.Count;
        OpenCount = _cards.Values.Count(c => !c.IsDone);
        var labels = _cards.Values.SelectMany(c => c.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!AvailableLabels.SequenceEqual(labels, StringComparer.Ordinal))
        {
            AvailableLabels.Clear();
            foreach (var label in labels)
            {
                AvailableLabels.Add(label);
            }
        }

        HasAvailableLabels = AvailableLabels.Count > 0;
        OnPropertyChanged(nameof(TotalText));
    }

    private void RefreshDates()
    {
        var now = Now;
        foreach (var card in _cards.Values)
        {
            card.Update(card.Item, now, CurrentBranch);
        }

        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnPriorityFilterChanged(TaskPriorityFilter value) => ApplyFilters();

    partial void OnLabelFilterChanged(string? value) => ApplyFilters();

    partial void OnShowDoneChanged(bool value) => ApplyFilters();

    partial void OnShowOlderDoneChanged(bool value) => ApplyFilters();

    partial void OnViewModeChanged(TasksViewMode value) => ApplyFilters();

    partial void OnSelectedCardChanged(TaskCardViewModel? value)
    {
        // Selecting another card while the details are open shows that card.
        if (!_openingDetail && value is not null && Detail is not null && !ReferenceEquals(Detail.Card, value))
        {
            _ = OpenDetailAsync(value);
        }
    }

    // ----- Live updates -----------------------------------------------------------------

    private void OnWorkItemsChanged(object? sender, WorkItemsChangedEventArgs e)
    {
        if (!string.Equals(e.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        Services.Dispatcher.Post(() =>
        {
            if (_disposed || !HasLoaded)
            {
                return;
            }

            if (IsActive)
            {
                _ = ReloadAsync();
            }
            else
            {
                _stale = true;
            }
        });
    }

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        var branch = Context.GitStatus?.Branch;
        if (string.Equals(branch, CurrentBranch, StringComparison.Ordinal))
        {
            Detail?.RefreshBranchState();
            return;
        }

        CurrentBranch = branch;
        foreach (var card in _cards.Values)
        {
            card.UpdateBranch(branch);
        }

        Detail?.RefreshBranchState();
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectContext.Project) && Detail is { } detail)
        {
            detail.Apply(detail.Card.Item);
        }
    }

    private async Task RecordActivityAsync(TaskCardViewModel card, string branch, bool created, bool switched)
    {
        try
        {
            await Services.Activity.RecordAsync(new ActivityEntry
            {
                ProjectId = Context.ProjectId,
                At = Now,
                Kind = created ? ActivityKind.GitBranchCreated : ActivityKind.GitCheckout,
                Outcome = ActivityOutcome.Success,
                Title = $"Started working on {card.Key} {card.Title}",
                Detail = created
                    ? switched ? $"Created and switched to {branch}" : $"Created {branch}"
                    : $"Switched to {branch}",
                RefKind = "work-item",
                RefValue = card.Id,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Journaling must never turn a successful action into a failure.
            _logger.LogWarning(ex, "Could not record the start of {Key}", card.Key);
        }
    }
}
