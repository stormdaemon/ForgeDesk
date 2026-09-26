using System.Globalization;

namespace ForgeDesk.Core.WorkItems;

/// <summary>Human-readable wording of work item fields and history events.</summary>
public static class WorkItemText
{
    public static string StatusName(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Backlog => "Backlog",
        WorkItemStatus.Todo => "To do",
        WorkItemStatus.InProgress => "In progress",
        WorkItemStatus.Review => "Review",
        WorkItemStatus.Done => "Done",
        _ => status.ToString(),
    };

    public static string PriorityName(WorkItemPriority priority) => priority switch
    {
        WorkItemPriority.None => "None",
        WorkItemPriority.Low => "Low",
        WorkItemPriority.Medium => "Medium",
        WorkItemPriority.High => "High",
        WorkItemPriority.Urgent => "Urgent",
        _ => priority.ToString(),
    };

    public static string LinkKindName(WorkItemLinkKind kind) => kind switch
    {
        WorkItemLinkKind.Branch => "branch",
        WorkItemLinkKind.Commit => "commit",
        WorkItemLinkKind.File => "file",
        WorkItemLinkKind.Issue => "issue",
        WorkItemLinkKind.PullRequest => "pull request",
        WorkItemLinkKind.Url => "link",
        _ => kind.ToString(),
    };

    /// <summary>"branch feature/login", "commit 3f2a9c1", "issue #12", "https://…".</summary>
    public static string DescribeLink(WorkItemLinkKind kind, string value) => kind switch
    {
        WorkItemLinkKind.Commit => $"commit {(value.Length > 7 ? value[..7] : value)}",
        WorkItemLinkKind.Issue or WorkItemLinkKind.PullRequest when value.All(char.IsAsciiDigit) => $"{LinkKindName(kind)} #{value}",
        WorkItemLinkKind.Url => value,
        _ => $"{LinkKindName(kind)} {value}",
    };

    /// <summary>Due dates are picked as calendar days, so they are shown in the user's time zone.</summary>
    public static string FormatDate(DateTimeOffset? date) =>
        date is { } d ? d.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "none";

    internal static string Transition(string field, string from, string to) => $"{field}: {from} → {to}";

    internal static string LabelsChange(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var added = after.Where(l => !before.Contains(l, StringComparer.Ordinal)).ToList();
        var removed = before.Where(l => !after.Contains(l, StringComparer.Ordinal)).ToList();
        var parts = new List<string>(2);
        if (added.Count > 0)
        {
            parts.Add("added " + string.Join(", ", added));
        }

        if (removed.Count > 0)
        {
            parts.Add("removed " + string.Join(", ", removed));
        }

        return "Labels: " + string.Join("; ", parts);
    }

    internal static string Quote(string text) => $"“{text}”";
}
