namespace ForgeDesk.Core.Activity;

/// <summary>Local, persistent journal of what happened through ForgeDesk.</summary>
public interface IActivityLog
{
    event EventHandler<ActivityEntry>? EntryAdded;

    /// <summary>Records an entry. Never throws: journaling must not break the operation it describes.</summary>
    Task<ActivityEntry?> RecordAsync(ActivityEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityEntry>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken = default);

    Task ClearAsync(string? projectId, CancellationToken cancellationToken = default);

    /// <summary>Keeps at most <paramref name="maxEntriesPerProject"/> entries per project.</summary>
    Task PruneAsync(int maxEntriesPerProject = 5000, CancellationToken cancellationToken = default);
}
