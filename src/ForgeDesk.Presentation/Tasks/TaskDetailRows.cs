using System.Globalization;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.WorkItems;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>Deep links into the Tasks tab (besides a work item id string, which opens that task).</summary>
public sealed record TaskNavigation(string? WorkItemId = null, bool NewTask = false)
{
    /// <summary>Focuses the quick-add box of "To do".</summary>
    public static TaskNavigation CreateTask { get; } = new(NewTask: true);

    public static TaskNavigation Open(string workItemId) => new(workItemId);
}

/// <summary>A link of the details pane.</summary>
public sealed record TaskLinkViewModel(WorkItemLink Link, GitHubRepoRef? GitHub)
{
    public string Id => Link.Id;

    public WorkItemLinkKind Kind => Link.Kind;

    public string Value => Link.Value;

    public string KindName => WorkItemText.LinkKindName(Link.Kind);

    /// <summary>What the row shows: the label, else a readable form of the value.</summary>
    public string Text => Link.Label ?? Link.Kind switch
    {
        WorkItemLinkKind.Commit => Link.Value.Length > 7 ? Link.Value[..7] : Link.Value,
        WorkItemLinkKind.Issue or WorkItemLinkKind.PullRequest when IsNumber => "#" + Link.Value,
        _ => Link.Value,
    };

    /// <summary>Secondary text: the kind, or the value when a label is shown.</summary>
    public string Detail => Link.Label is null ? Capitalize(KindName) : $"{Capitalize(KindName)} · {Link.Value}";

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Link.Kind switch
    {
        WorkItemLinkKind.Branch => "BranchFork16",
        WorkItemLinkKind.Commit => "Record16",
        WorkItemLinkKind.File => "Document16",
        WorkItemLinkKind.Issue => "Bug16",
        WorkItemLinkKind.PullRequest => "BranchRequest16",
        _ => "Link16",
    };

    private bool IsNumber => Link.Value.All(char.IsAsciiDigit) && Link.Value.Length > 0;

    /// <summary>The GitHub page of an issue / pull request, when the project has a GitHub remote.</summary>
    public string? Url => Link.Kind switch
    {
        WorkItemLinkKind.Url => Link.Value,
        WorkItemLinkKind.Issue when GitHub is not null && IsNumber => $"{GitHub.HtmlUrl}/issues/{Link.Value}",
        WorkItemLinkKind.PullRequest when GitHub is not null && IsNumber => $"{GitHub.HtmlUrl}/pull/{Link.Value}",
        _ => null,
    };

    public bool CanOpen => Link.Kind is WorkItemLinkKind.Branch or WorkItemLinkKind.Commit or WorkItemLinkKind.File || Url is not null;

    public string ToolTip => Link.Kind switch
    {
        WorkItemLinkKind.Branch => $"Show the branch {Link.Value} in Git",
        WorkItemLinkKind.Commit => $"Show commit {Text} in the Git history",
        WorkItemLinkKind.File => $"Show {Link.Value} in Files",
        WorkItemLinkKind.Issue or WorkItemLinkKind.PullRequest when Url is null =>
            "The project has no GitHub remote, so this number can't be opened.",
        _ => $"Open {Url}",
    };

    public string AutomationId => $"Tasks.Link.{Link.Kind}.{Link.Id}";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}

/// <summary>An event of the history timeline.</summary>
public sealed record TaskEventViewModel(WorkItemEvent Event)
{
    public string Summary => Event.Summary;

    public DateTimeOffset At => Event.At;

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Event.Kind switch
    {
        WorkItemEventKind.Created => "Add16",
        WorkItemEventKind.TitleChanged => "Edit16",
        WorkItemEventKind.DescriptionChanged => "TextDescription16",
        WorkItemEventKind.StatusChanged => "ArrowRight16",
        WorkItemEventKind.PriorityChanged => "Flag16",
        WorkItemEventKind.LabelsChanged => "Tag16",
        WorkItemEventKind.LinkAdded => "Link16",
        WorkItemEventKind.LinkRemoved => "Dismiss16",
        WorkItemEventKind.DueDateChanged => "Calendar16",
        WorkItemEventKind.Reopened => "ArrowUndo16",
        _ => "Info16",
    };
}
