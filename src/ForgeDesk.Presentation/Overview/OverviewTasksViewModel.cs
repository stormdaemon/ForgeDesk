using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

public sealed record OverviewTaskStatusCount(WorkItemStatus Status, string Label, int Count);

public sealed record OverviewTaskViewModel(string Id, string Key, string Title, WorkItemStatus Status, WorkItemPriority Priority, DateTimeOffset? DueAt)
{
    public string StatusLabel => OverviewTasksViewModel.StatusLabel(Status);

    public string PriorityLabel => Priority switch
    {
        WorkItemPriority.Urgent => "Urgent",
        WorkItemPriority.High => "High",
        WorkItemPriority.Medium => "Medium",
        WorkItemPriority.Low => "Low",
        _ => string.Empty,
    };

    public bool HasPriority => Priority != WorkItemPriority.None;

    public StatusTone PriorityTone => Priority switch
    {
        WorkItemPriority.Urgent => StatusTone.Danger,
        WorkItemPriority.High => StatusTone.Warning,
        WorkItemPriority.Medium => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    public string ToolTip => $"{Key} {Title}\n{StatusLabel}{(HasPriority ? " · " + PriorityLabel + " priority" : string.Empty)}\nClick to open it in Tasks";

    public static OverviewTaskViewModel From(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new OverviewTaskViewModel(item.Id, item.Key, item.Title, item.Status, item.Priority, item.DueAt);
    }
}

/// <summary>"Tasks" card: open tasks per status and the most important ones.</summary>
public sealed partial class OverviewTasksViewModel : OverviewCardViewModel
{
    internal const int TopCount = 5;

    private static readonly WorkItemStatus[] OpenStatuses = [WorkItemStatus.InProgress, WorkItemStatus.Review, WorkItemStatus.Todo, WorkItemStatus.Backlog];

    private readonly IWorkItemService _workItems;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;

    public OverviewTasksViewModel(ProjectContext context, IWorkItemService workItems, IDialogService dialogs, INotificationService notifications, IUiDispatcher dispatcher)
        : base(context)
    {
        _workItems = workItems;
        _dialogs = dialogs;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _workItems.Changed += OnWorkItemsChanged;
    }

    public ObservableCollection<OverviewTaskStatusCount> StatusCounts { get; } = [];

    public ObservableCollection<OverviewTaskViewModel> TopTasks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenText))]
    public partial int OpenCount { get; private set; }

    [ObservableProperty]
    public partial int DoneCount { get; private set; }

    /// <summary>The project has no task at all.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Every task is done.</summary>
    [ObservableProperty]
    public partial bool IsAllDone { get; private set; }

    public string OpenText => Format.Count(OpenCount, "open task");

    protected override string ErrorTitle => "Could not load the tasks";

    internal static string StatusLabel(WorkItemStatus status) => status switch
    {
        WorkItemStatus.InProgress => "In progress",
        WorkItemStatus.Todo => "To do",
        _ => status.ToString(),
    };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var items = await _workItems.GetAllAsync(Context.ProjectId, cancellationToken).ConfigureAwait(true);
        var open = items.Where(i => i.IsOpen).ToList();
        OpenCount = open.Count;
        DoneCount = items.Count - open.Count;
        IsEmpty = items.Count == 0;
        IsAllDone = items.Count > 0 && open.Count == 0;

        var counts = OpenStatuses
            .Select(s => new OverviewTaskStatusCount(s, StatusLabel(s), open.Count(i => i.Status == s)))
            .Where(c => c.Count > 0)
            .ToList();
        Replace(StatusCounts, counts);
        Replace(TopTasks, SelectTop(open).Select(OverviewTaskViewModel.From).ToList());
    }

    /// <summary>Most important open tasks: priority, then in-progress first, then due date, then board order.</summary>
    internal static IEnumerable<WorkItem> SelectTop(IEnumerable<WorkItem> open) =>
        open.OrderByDescending(i => i.Priority)
            .ThenBy(i => Array.IndexOf(OpenStatuses, i.Status))
            .ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Number)
            .Take(TopCount);

    [RelayCommand]
    private void OpenTask(OverviewTaskViewModel? task)
    {
        if (task is not null)
        {
            Context.RequestNavigation(WorkspaceSection.Tasks, task.Id);
        }
    }

    [RelayCommand]
    private void OpenTasks() => Context.RequestNavigation(WorkspaceSection.Tasks);

    [RelayCommand]
    private async Task NewTaskAsync()
    {
        var title = await _dialogs.PromptAsync(new PromptOptions
        {
            Title = "New task",
            Message = "What needs to be done? You can add details, priority and labels in the Tasks tab.",
            Placeholder = "Fix the flaky login test",
            ConfirmText = "Create task",
            Validate = text => string.IsNullOrWhiteSpace(text) ? "Give the task a title." : null,
        }).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var created = await _workItems.CreateAsync(Context.ProjectId, new WorkItemDraft(title.Trim()), Context.Lifetime).ConfigureAwait(true);
            _notifications.Show($"Created task {created.Key}", created.Title, NotificationSeverity.Success,
                new NotificationAction("Open", () =>
                {
                    Context.RequestNavigation(WorkspaceSection.Tasks, created.Id);
                    return Task.CompletedTask;
                }));
            await LoadAsync().ConfigureAwait(true);
        }, errorTitle: "Could not create the task", errorMode: ErrorMode.Toast, notifications: _notifications).ConfigureAwait(true);
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void OnWorkItemsChanged(object? sender, WorkItemsChangedEventArgs e)
    {
        if (string.Equals(e.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            _dispatcher.Post(RequestReload);
        }
    }

    protected override void OnDispose() => _workItems.Changed -= OnWorkItemsChanged;
}
