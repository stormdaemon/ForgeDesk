using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Activity;

/// <summary>Families of <see cref="ActivityKind"/> used for icons and filter chips.</summary>
public enum ActivityGroup
{
    All,
    Git,
    Commands,
    Releases,
    Tasks,
    GitHub,
    Projects,
    Other,
}

public static class ActivityGroups
{
    /// <summary>Groups offered as filter chips, in display order.</summary>
    public static IReadOnlyList<ActivityGroup> Filters { get; } =
        [ActivityGroup.All, ActivityGroup.Git, ActivityGroup.Commands, ActivityGroup.Releases, ActivityGroup.Tasks, ActivityGroup.GitHub, ActivityGroup.Projects];

    public static ActivityGroup Of(ActivityKind kind) => kind switch
    {
        ActivityKind.ProjectAdded or ActivityKind.ProjectRemoved or ActivityKind.ProjectCloned or ActivityKind.ProjectRelocated => ActivityGroup.Projects,
        >= ActivityKind.GitCommit and <= ActivityKind.GitTagCreated => ActivityGroup.Git,
        ActivityKind.RunCompleted => ActivityGroup.Commands,
        ActivityKind.ReleasePublished or ActivityKind.ReleaseFailed => ActivityGroup.Releases,
        ActivityKind.WorkItemCreated or ActivityKind.WorkItemCompleted or ActivityKind.WorkItemUpdated => ActivityGroup.Tasks,
        >= ActivityKind.IssueCreated and <= ActivityKind.WorkflowCancelled => ActivityGroup.GitHub,
        _ => ActivityGroup.Other,
    };

    /// <summary>The kinds of a group, for <see cref="ActivityQuery.Kinds"/>; null for <see cref="ActivityGroup.All"/>.</summary>
    public static IReadOnlyList<ActivityKind>? KindsOf(ActivityGroup group) =>
        group == ActivityGroup.All ? null : Enum.GetValues<ActivityKind>().Where(k => Of(k) == group).ToArray();

    public static string Title(ActivityGroup group) => group switch
    {
        ActivityGroup.All => "All",
        ActivityGroup.GitHub => "GitHub",
        _ => group.ToString(),
    };

    /// <summary>WPF-UI SymbolRegular name of the group.</summary>
    public static string Icon(ActivityGroup group) => group switch
    {
        ActivityGroup.Git => "BranchFork20",
        ActivityGroup.Commands => "Play20",
        ActivityGroup.Releases => "Rocket20",
        ActivityGroup.Tasks => "TaskListSquareLtr20",
        ActivityGroup.GitHub => "Globe20",
        ActivityGroup.Projects => "Folder20",
        ActivityGroup.Other => "ErrorCircle20",
        _ => "History20",
    };

    public static StatusTone ToneOf(ActivityOutcome outcome) => outcome switch
    {
        ActivityOutcome.Success => StatusTone.Success,
        ActivityOutcome.Warning => StatusTone.Warning,
        ActivityOutcome.Failure => StatusTone.Danger,
        _ => StatusTone.Neutral,
    };
}
