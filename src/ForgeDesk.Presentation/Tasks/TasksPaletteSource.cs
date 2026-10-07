using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>
/// Tasks of the project on screen ("#" mode, or any search of two characters or more) — opening
/// one shows it in the Tasks tab — and the "New task…" action ("&gt;" mode).
/// </summary>
public sealed class TasksPaletteSource : IPaletteSource
{
    public const int MaxResults = 20;

    private readonly INavigationService _navigation;
    private readonly IWorkItemService _workItems;
    private readonly ILogger<TasksPaletteSource> _logger;

    public TasksPaletteSource(INavigationService navigation, IWorkItemService workItems, ILogger<TasksPaletteSource> logger)
    {
        _navigation = navigation;
        _workItems = workItems;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.Tasks))
        {
            return [];
        }

        var items = new List<PaletteItem>();
        if (query.Wants(PaletteCategory.Action))
        {
            items.Add(new PaletteItem
            {
                Title = "New task…",
                Subtitle = workspace.Name,
                Icon = "TaskListSquareAdd20",
                Category = PaletteCategory.Action,
                Shortcut = "Ctrl+N",
                Keywords = "create add todo task work item board",
                Boost = 1,
                Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Tasks, TaskNavigation.CreateTask),
            });
        }

        // Searching every task on each keystroke is only worth it in "#" mode or with a real search term.
        var explicitMode = query.Categories is { } categories && categories.Contains(PaletteCategory.Task);
        if (query.Wants(PaletteCategory.Task) && (explicitMode || query.Text.Trim().Length >= 2))
        {
            items.AddRange(await SearchAsync(workspace, query.Text, cancellationToken).ConfigureAwait(false));
        }

        return items;
    }

    private async Task<IEnumerable<PaletteItem>> SearchAsync(ProjectWorkspaceViewModel workspace, string text, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkItem> found;
        try
        {
            found = await _workItems.SearchAsync(workspace.ProjectId, text.Trim(), MaxResults, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not search tasks for the palette");
            return [];
        }

        return found.Select((item, index) => new PaletteItem
        {
            Title = $"{item.Key} {item.Title}",
            Subtitle = $"{WorkItemText.StatusName(item.Status)} · {workspace.Name}"
                + (item.Labels.Count > 0 ? " · " + string.Join(", ", item.Labels) : string.Empty),
            Icon = item.Status == WorkItemStatus.Done ? "CheckmarkCircle20" : "TaskListSquareLtr20",
            Category = PaletteCategory.Task,
            // The palette ranks by its own fuzzy match; the description is matched by the service, so expose it too.
            Keywords = $"{item.Key} {string.Join(' ', item.Labels)} {WorkItemText.StatusName(item.Status)} {Excerpt(item.Description)}",
            Boost = Math.Max(0, 2 - (index * 0.1)) + (item.IsOpen ? 0.5 : 0),
            Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Tasks, item.Id),
        });
    }

    private static string Excerpt(string description) =>
        description.Length > 200 ? description[..200] : description;
}
