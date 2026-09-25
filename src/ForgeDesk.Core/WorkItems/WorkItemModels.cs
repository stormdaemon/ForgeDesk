namespace ForgeDesk.Core.WorkItems;

public enum WorkItemStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Review = 3,
    Done = 4,
}

public enum WorkItemPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

public enum WorkItemLinkKind
{
    Branch = 0,
    Commit = 1,
    File = 2,
    Issue = 3,
    PullRequest = 4,
    Url = 5,
}

public enum WorkItemEventKind
{
    Created = 0,
    TitleChanged,
    DescriptionChanged,
    StatusChanged,
    PriorityChanged,
    LabelsChanged,
    LinkAdded,
    LinkRemoved,
    DueDateChanged,
    Reopened,
}

public sealed record WorkItemLink(string Id, WorkItemLinkKind Kind, string Value, string? Label, DateTimeOffset CreatedAt);

public sealed record WorkItemEvent(long Id, DateTimeOffset At, WorkItemEventKind Kind, string Summary, string? OldValue, string? NewValue);

/// <summary>A developer task ("work item" in code to avoid clashing with System.Threading.Tasks).</summary>
public sealed record WorkItem
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }

    /// <summary>Per-project sequential number, displayed as "#12".</summary>
    public int Number { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public WorkItemStatus Status { get; init; }
    public WorkItemPriority Priority { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>Order within its status column.</summary>
    public double SortOrder { get; init; }
    public IReadOnlyList<WorkItemLink> Links { get; init; } = [];

    public string Key => $"#{Number}";
    public bool IsOpen => Status != WorkItemStatus.Done;
}

public sealed record WorkItemDraft(string Title, string Description = "", WorkItemStatus Status = WorkItemStatus.Todo, WorkItemPriority Priority = WorkItemPriority.None, IReadOnlyList<string>? Labels = null, DateTimeOffset? DueAt = null);

/// <summary>Partial update: null properties are left unchanged.</summary>
public sealed record WorkItemPatch
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public WorkItemStatus? Status { get; init; }
    public WorkItemPriority? Priority { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool ClearDueDate { get; init; }
}

public sealed record WorkItemsChangedEventArgs(string ProjectId, string? WorkItemId);
