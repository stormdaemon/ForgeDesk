using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>A task as a board card and a table row. Updated in place when the task changes, so lists keep their containers.</summary>
public sealed partial class TaskCardViewModel : ObservableObject
{
    public TaskCardViewModel(WorkItem item, DateTimeOffset now, string? currentBranch = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Update(item, now, currentBranch);
    }

    public string Id => Item.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key), nameof(Number), nameof(Title), nameof(Status), nameof(StatusName), nameof(Priority), nameof(PriorityName),
        nameof(PriorityTone), nameof(HasPriority), nameof(Labels), nameof(HasLabels), nameof(LabelsText), nameof(Description), nameof(UpdatedAt), nameof(CreatedAt),
        nameof(DueAt), nameof(HasDue), nameof(IsDone), nameof(BranchCount), nameof(CommitCount), nameof(FileCount), nameof(IssueCount), nameof(UrlCount),
        nameof(HasLinks), nameof(LinksToolTip), nameof(SortOrder), nameof(ToolTip), nameof(AutomationId))]
    public partial WorkItem Item { get; private set; }

    public string Key => Item.Key;

    public int Number => Item.Number;

    public string Title => Item.Title;

    public string Description => Item.Description;

    public WorkItemStatus Status => Item.Status;

    public string StatusName => WorkItemText.StatusName(Item.Status);

    public WorkItemPriority Priority => Item.Priority;

    public string PriorityName => WorkItemText.PriorityName(Item.Priority);

    public StatusTone PriorityTone => TaskPriorityOption.ToneOf(Item.Priority);

    public bool HasPriority => Item.Priority != WorkItemPriority.None;

    public IReadOnlyList<string> Labels => Item.Labels;

    public bool HasLabels => Item.Labels.Count > 0;

    public string LabelsText => string.Join(", ", Item.Labels);

    public DateTimeOffset UpdatedAt => Item.UpdatedAt;

    public DateTimeOffset CreatedAt => Item.CreatedAt;

    public DateTimeOffset? DueAt => Item.DueAt;

    public bool HasDue => Item.DueAt is not null;

    public bool IsDone => Item.Status == WorkItemStatus.Done;

    public double SortOrder => Item.SortOrder;

    public int BranchCount => Item.Links.Count(l => l.Kind == WorkItemLinkKind.Branch);

    public int CommitCount => Item.Links.Count(l => l.Kind == WorkItemLinkKind.Commit);

    public int FileCount => Item.Links.Count(l => l.Kind == WorkItemLinkKind.File);

    /// <summary>Issues and pull requests.</summary>
    public int IssueCount => Item.Links.Count(l => l.Kind is WorkItemLinkKind.Issue or WorkItemLinkKind.PullRequest);

    public int UrlCount => Item.Links.Count(l => l.Kind == WorkItemLinkKind.Url);

    public bool HasLinks => Item.Links.Count > 0;

    public string LinksToolTip => string.Join("\n", Item.Links.Select(l => WorkItemText.DescribeLink(l.Kind, l.Value)));

    public string ToolTip => $"{Key} {Title}" + (Item.Description.Length > 0 ? "\n\n" + Excerpt(Item.Description) : string.Empty);

    /// <summary>UI automation id of the card ("Tasks.Card.12").</summary>
    public string AutomationId => $"Tasks.Card.{Item.Number}";

    public override string ToString() => $"{Key} {Title}";

    /// <summary>"Due today", "Overdue by 3 days"…</summary>
    [ObservableProperty]
    public partial string DueText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOverdue { get; private set; }

    /// <summary>The task is linked to the branch checked out now.</summary>
    [ObservableProperty]
    public partial bool IsOnCurrentBranch { get; private set; }

    /// <summary>The card is shown in the details pane.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; internal set; }

    /// <summary>A start / complete action is running for this task.</summary>
    [ObservableProperty]
    public partial bool IsWorking { get; internal set; }

    public void Update(WorkItem item, DateTimeOffset now, string? currentBranch)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item != Item)
        {
            Item = item;
        }

        DueText = item.DueAt is { } due ? TaskBoardMath.DueText(due, now, IsDone) : string.Empty;
        IsOverdue = TaskBoardMath.IsOverdue(item.DueAt, now, IsDone);
        UpdateBranch(currentBranch);
    }

    public void UpdateBranch(string? currentBranch) =>
        IsOnCurrentBranch = currentBranch is not null
            && Item.Links.Any(l => l.Kind == WorkItemLinkKind.Branch && string.Equals(l.Value, currentBranch, StringComparison.Ordinal));

    /// <summary>Local filter: every word must appear in the key, title, description or labels.</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            (term.StartsWith('#') && term.Length > 1 && string.Equals(term[1..], Number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            || Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Labels.Any(l => l.Contains(term, StringComparison.OrdinalIgnoreCase))
            || string.Equals(term, Number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    private static string Excerpt(string text)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length > 240 ? flattened[..240] + "…" : flattened;
    }
}
