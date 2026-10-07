using System.Globalization;
using System.Text;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>Where a dragged card lands: the cards it goes between (null at a column edge).</summary>
public sealed record TaskDropPosition(string? AfterId, string? BeforeId, int Index, bool IsUnchanged);

/// <summary>A drop of <see cref="Card"/> on the column <see cref="Status"/>, at <see cref="Index"/> in the column as displayed.</summary>
public sealed record TaskDropRequest(TaskCardViewModel Card, WorkItemStatus Status, int Index);

/// <summary>Pure helpers of the board: drop neighbours and branch names.</summary>
public static class TaskBoardMath
{
    public const int MaxSlugLength = 48;

    /// <summary>
    /// Neighbours of a card dropped at <paramref name="dropIndex"/> of a column showing
    /// <paramref name="columnIds"/> (which may include the moved card itself when it is moved within
    /// its column). The index is an insertion point: 0 = top, Count = bottom.
    /// </summary>
    public static TaskDropPosition Neighbours(IReadOnlyList<string> columnIds, string movedId, int dropIndex)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        ArgumentNullException.ThrowIfNull(movedId);
        var ids = columnIds.ToList();
        var from = ids.IndexOf(movedId);
        var index = Math.Clamp(dropIndex, 0, ids.Count);
        if (from >= 0)
        {
            ids.RemoveAt(from);
            if (from < index)
            {
                index--;
            }
        }

        index = Math.Clamp(index, 0, ids.Count);
        var after = index > 0 ? ids[index - 1] : null;
        var before = index < ids.Count ? ids[index] : null;
        return new TaskDropPosition(after, before, index, IsUnchanged: from >= 0 && from == index);
    }

    /// <summary>"task/12-fix-login-redirect" for task #12 "Fix login redirect!".</summary>
    public static string BranchName(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var slug = Slug(item.Title);
        var number = item.Number.ToString(CultureInfo.InvariantCulture);
        return slug.Length == 0 ? $"task/{number}" : $"task/{number}-{slug}";
    }

    /// <summary>Lower-case ASCII words joined by '-', accents removed, at most <see cref="MaxSlugLength"/> characters, cut at a word boundary.</summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingDash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = builder.ToString();
        if (slug.Length > MaxSlugLength)
        {
            var cut = slug.LastIndexOf('-', MaxSlugLength);
            slug = cut > MaxSlugLength / 2 ? slug[..cut] : slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug;
    }

    /// <summary>"Due today", "Due tomorrow", "Overdue by 3 days", "Due Oct 12".</summary>
    public static string DueText(DateTimeOffset due, DateTimeOffset now, bool isDone)
    {
        var days = (due.ToLocalTime().Date - now.ToLocalTime().Date).Days;
        if (isDone)
        {
            return "Due " + WorkItemText.FormatDate(due);
        }

        return days switch
        {
            0 => "Due today",
            1 => "Due tomorrow",
            -1 => "Overdue by 1 day",
            < -1 => $"Overdue by {Format.Count(-days, "day")}",
            < 7 => "Due " + due.ToLocalTime().ToString("dddd", CultureInfo.InvariantCulture),
            _ => "Due " + due.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Past due: the due day is before today and the task is not done.</summary>
    public static bool IsOverdue(DateTimeOffset? due, DateTimeOffset now, bool isDone) =>
        !isDone && due is { } d && d.ToLocalTime().Date < now.ToLocalTime().Date;
}

/// <summary>A status choice (ComboBox, "Move to" menu).</summary>
public sealed record TaskStatusOption(WorkItemStatus Status, string Name)
{
    public static IReadOnlyList<TaskStatusOption> All { get; } =
        Enum.GetValues<WorkItemStatus>().Select(s => new TaskStatusOption(s, WorkItemText.StatusName(s))).ToArray();

    public override string ToString() => Name;
}

/// <summary>A priority choice with its color tone.</summary>
public sealed record TaskPriorityOption(WorkItemPriority Priority, string Name, StatusTone Tone)
{
    public static IReadOnlyList<TaskPriorityOption> All { get; } =
        new[] { WorkItemPriority.Urgent, WorkItemPriority.High, WorkItemPriority.Medium, WorkItemPriority.Low, WorkItemPriority.None }
            .Select(p => new TaskPriorityOption(p, WorkItemText.PriorityName(p), ToneOf(p)))
            .ToArray();

    public static StatusTone ToneOf(WorkItemPriority priority) => priority switch
    {
        WorkItemPriority.Urgent => StatusTone.Danger,
        WorkItemPriority.High => StatusTone.Warning,
        WorkItemPriority.Medium => StatusTone.Info,
        WorkItemPriority.Low => StatusTone.Neutral,
        _ => StatusTone.None,
    };

    public override string ToString() => Name;
}

/// <summary>A priority filter entry ("Any priority", "Urgent"…).</summary>
public sealed record TaskPriorityFilter(WorkItemPriority? Priority, string Name)
{
    public static TaskPriorityFilter Any { get; } = new(null, "Any priority");

    public static IReadOnlyList<TaskPriorityFilter> All { get; } =
        [Any, .. TaskPriorityOption.All.Select(o => new TaskPriorityFilter(o.Priority, o.Priority == WorkItemPriority.None ? "No priority" : o.Name))];

    public override string ToString() => Name;
}
