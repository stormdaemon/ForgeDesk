namespace ForgeDesk.Core.WorkItems;

public interface IWorkItemService
{
    event EventHandler<WorkItemsChangedEventArgs>? Changed;

    Task<IReadOnlyList<WorkItem>> GetAllAsync(string projectId, CancellationToken cancellationToken = default);

    Task<WorkItem?> GetAsync(string workItemId, CancellationToken cancellationToken = default);

    Task<int> CountOpenAsync(string projectId, CancellationToken cancellationToken = default);

    Task<WorkItem> CreateAsync(string projectId, WorkItemDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Applies the patch and records one history event per changed field.</summary>
    Task<WorkItem> UpdateAsync(string workItemId, WorkItemPatch patch, CancellationToken cancellationToken = default);

    /// <summary>Moves an item to a column, placing it between two neighbours (ids may be null).</summary>
    Task<WorkItem> MoveAsync(string workItemId, WorkItemStatus status, string? afterId, string? beforeId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string workItemId, CancellationToken cancellationToken = default);

    Task<WorkItem> AddLinkAsync(string workItemId, WorkItemLinkKind kind, string value, string? label = null, CancellationToken cancellationToken = default);

    Task<WorkItem> RemoveLinkAsync(string workItemId, string linkId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItemEvent>> GetHistoryAsync(string workItemId, CancellationToken cancellationToken = default);

    /// <summary>Items whose text mentions <paramref name="query"/> (for the command palette).</summary>
    Task<IReadOnlyList<WorkItem>> SearchAsync(string projectId, string query, int max = 20, CancellationToken cancellationToken = default);
}
