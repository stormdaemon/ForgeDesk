using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ForgeDesk.Core.WorkItems;

/// <summary>SQLite-backed task board (tables <c>work_items</c>, <c>work_item_links</c>, <c>work_item_events</c>).</summary>
internal sealed class WorkItemService : IWorkItemService
{
    internal const string ActivityRefKind = "work-item";
    internal const int MaxSearchResults = 200;

    private const string ItemColumns =
        "id, project_id, number, title, description, status, priority, labels, created_at, updated_at, completed_at, due_at, sort_order";

    private const string UpdateItemSql =
        """
        UPDATE work_items SET
            title = @Title, description = @Description, status = @Status, priority = @Priority, labels = @Labels,
            updated_at = @UpdatedAt, completed_at = @CompletedAt, due_at = @DueAt, sort_order = @SortOrder
        WHERE id = @Id
        """;

    private readonly Database _database;
    private readonly IActivityLog _activity;
    private readonly IClock _clock;

    // Serializes writers of this process; BEGIN IMMEDIATE covers writers in other processes.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public WorkItemService(Database database, IActivityLog activity, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _activity = activity;
        _clock = clock;
    }

    public event EventHandler<WorkItemsChangedEventArgs>? Changed;

    public Task<IReadOnlyList<WorkItem>> GetAllAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _database.UseAsync<IReadOnlyList<WorkItem>>(async c =>
        {
            var rows = await c.QueryAsync<WorkItemRow>(new CommandDefinition(
                $"SELECT {ItemColumns} FROM work_items WHERE project_id = @ProjectId ORDER BY status, sort_order, number",
                new { ProjectId = projectId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var links = await c.QueryAsync<LinkRow>(new CommandDefinition(
                """
                SELECT l.id, l.work_item_id, l.kind, l.value, l.label, l.created_at
                FROM work_item_links l JOIN work_items w ON w.id = l.work_item_id
                WHERE w.project_id = @ProjectId
                ORDER BY l.created_at, l.id
                """,
                new { ProjectId = projectId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return Assemble(rows, links);
        }, cancellationToken);
    }

    public Task<WorkItem?> GetAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        return _database.UseAsync(c => LoadAsync(c, null, workItemId, cancellationToken), cancellationToken);
    }

    public Task<int> CountOpenAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _database.UseAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_items WHERE project_id = @ProjectId AND status <> @Done",
            new { ProjectId = projectId, Done = (int)WorkItemStatus.Done }, cancellationToken: cancellationToken)), cancellationToken);
    }

    public async Task<WorkItem> CreateAsync(string projectId, WorkItemDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(draft);
        var title = WorkItemRules.NormalizeTitle(draft.Title);
        var description = WorkItemRules.NormalizeDescription(draft.Description);
        var labels = WorkItemRules.NormalizeLabels(draft.Labels);
        WorkItemRules.EnsureDefined(draft.Status);
        WorkItemRules.EnsureDefined(draft.Priority);

        var created = await WriteAsync(async (c, tx) =>
        {
            var projectExists = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE id = @ProjectId",
                new { ProjectId = projectId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (projectExists == 0)
            {
                throw new ForgeException(ErrorKind.NotFound, "This project is no longer registered in ForgeDesk.",
                    "Add the project again to manage its tasks.");
            }

            var number = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE(MAX(number), 0) + 1 FROM work_items WHERE project_id = @ProjectId",
                new { ProjectId = projectId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var now = Now();
            var item = new WorkItem
            {
                Id = Ids.New(),
                ProjectId = projectId,
                Number = checked((int)number),
                Title = title,
                Description = description,
                Status = draft.Status,
                Priority = draft.Priority,
                Labels = labels,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = draft.Status == WorkItemStatus.Done ? now : null,
                DueAt = draft.DueAt?.ToUniversalTime(),
                SortOrder = await EndOfColumnAsync(c, tx, projectId, draft.Status, cancellationToken).ConfigureAwait(false),
            };

            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO work_items (id, project_id, number, title, description, status, priority, labels,
                                        created_at, updated_at, completed_at, due_at, sort_order)
                VALUES (@Id, @ProjectId, @Number, @Title, @Description, @Status, @Priority, @Labels,
                        @CreatedAt, @UpdatedAt, @CompletedAt, @DueAt, @SortOrder)
                """,
                WorkItemRow.From(item), tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await InsertEventsAsync(c, tx, item.Id, now,
                [new PendingEvent(WorkItemEventKind.Created, $"Created in {WorkItemText.StatusName(item.Status)}", null, item.Title)],
                cancellationToken).ConfigureAwait(false);
            return item;
        }, cancellationToken).ConfigureAwait(false);

        await _activity.RecordAsync(new ActivityEntry
        {
            ProjectId = created.ProjectId,
            Kind = ActivityKind.WorkItemCreated,
            Outcome = ActivityOutcome.Info,
            Title = $"Created task {created.Key}: {created.Title}",
            RefKind = ActivityRefKind,
            RefValue = created.Id,
        }, CancellationToken.None).ConfigureAwait(false);
        RaiseChanged(created);
        return created;
    }

    public async Task<WorkItem> UpdateAsync(string workItemId, WorkItemPatch patch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentNullException.ThrowIfNull(patch);
        var title = patch.Title is null ? null : WorkItemRules.NormalizeTitle(patch.Title);
        var description = patch.Description is null ? null : WorkItemRules.NormalizeDescription(patch.Description);
        var labels = patch.Labels is null ? null : WorkItemRules.NormalizeLabels(patch.Labels);
        if (patch.Status is { } requestedStatus)
        {
            WorkItemRules.EnsureDefined(requestedStatus);
        }

        if (patch.Priority is { } requestedPriority)
        {
            WorkItemRules.EnsureDefined(requestedPriority);
        }

        var mutation = await WriteAsync(async (c, tx) =>
        {
            var current = await LoadAsync(c, tx, workItemId, cancellationToken).ConfigureAwait(false) ?? throw TaskNotFound();
            var now = Now();
            var events = new List<PendingEvent>();
            var updated = current;

            if (title is not null && !string.Equals(title, current.Title, StringComparison.Ordinal))
            {
                events.Add(new PendingEvent(WorkItemEventKind.TitleChanged,
                    WorkItemText.Transition("Title", WorkItemText.Quote(current.Title), WorkItemText.Quote(title)), current.Title, title));
                updated = updated with { Title = title };
            }

            if (description is not null && !string.Equals(description, current.Description, StringComparison.Ordinal))
            {
                var summary = current.Description.Length == 0 ? "Description added"
                    : description.Length == 0 ? "Description removed"
                    : "Description edited";
                events.Add(new PendingEvent(WorkItemEventKind.DescriptionChanged, summary, null, null));
                updated = updated with { Description = description };
            }

            if (patch.Status is { } status && status != current.Status)
            {
                // A card whose status changes from the details pane lands at the bottom of its new column.
                updated = ChangeStatus(updated, status, now, events) with
                {
                    SortOrder = await EndOfColumnAsync(c, tx, current.ProjectId, status, cancellationToken).ConfigureAwait(false),
                };
            }

            if (patch.Priority is { } priority && priority != current.Priority)
            {
                events.Add(new PendingEvent(WorkItemEventKind.PriorityChanged,
                    WorkItemText.Transition("Priority", WorkItemText.PriorityName(current.Priority), WorkItemText.PriorityName(priority)),
                    current.Priority.ToString(), priority.ToString()));
                updated = updated with { Priority = priority };
            }

            if (labels is not null && !new HashSet<string>(current.Labels, StringComparer.Ordinal).SetEquals(labels))
            {
                events.Add(new PendingEvent(WorkItemEventKind.LabelsChanged, WorkItemText.LabelsChange(current.Labels, labels),
                    WorkItemRules.SerializeLabels(current.Labels), WorkItemRules.SerializeLabels(labels)));
                updated = updated with { Labels = labels };
            }

            var dueAt = patch.ClearDueDate ? null : patch.DueAt?.ToUniversalTime() ?? current.DueAt;
            if (dueAt != current.DueAt)
            {
                events.Add(new PendingEvent(WorkItemEventKind.DueDateChanged,
                    WorkItemText.Transition("Due date", WorkItemText.FormatDate(current.DueAt), WorkItemText.FormatDate(dueAt)),
                    FormatStored(current.DueAt), FormatStored(dueAt)));
                updated = updated with { DueAt = dueAt };
            }

            if (events.Count == 0)
            {
                return new Mutation(current, Changed: false, PreviousStatus: null);
            }

            updated = updated with { UpdatedAt = now };
            await c.ExecuteAsync(new CommandDefinition(UpdateItemSql, WorkItemRow.From(updated), tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await InsertEventsAsync(c, tx, updated.Id, now, events, cancellationToken).ConfigureAwait(false);
            return new Mutation(updated, Changed: true, PreviousStatus: updated.Status != current.Status ? current.Status : null);
        }, cancellationToken).ConfigureAwait(false);

        return await CompleteMutationAsync(mutation).ConfigureAwait(false);
    }

    public async Task<WorkItem> MoveAsync(string workItemId, WorkItemStatus status, string? afterId, string? beforeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        WorkItemRules.EnsureDefined(status);

        var mutation = await WriteAsync(async (c, tx) =>
        {
            var current = await LoadAsync(c, tx, workItemId, cancellationToken).ConfigureAwait(false) ?? throw TaskNotFound();
            var column = (await c.QueryAsync<ColumnSlot>(new CommandDefinition(
                """
                SELECT id, sort_order, number FROM work_items
                WHERE project_id = @ProjectId AND status = @Status AND id <> @Id
                ORDER BY sort_order, number
                """,
                new { current.ProjectId, Status = (int)status, current.Id }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var index = ResolveInsertIndex(column, afterId, beforeId);
            if (status == current.Status && index == PositionOf(current, column))
            {
                return new Mutation(current, Changed: false, PreviousStatus: null);
            }

            var sortOrder = SortOrderMath.Between(
                index > 0 ? column[index - 1].SortOrder : null,
                index < column.Count ? column[index].SortOrder : null)
                ?? await RenumberColumnAsync(c, tx, column, index, cancellationToken).ConfigureAwait(false);

            var events = new List<PendingEvent>();
            var moved = current with { SortOrder = sortOrder };
            if (status != current.Status)
            {
                var now = Now();
                moved = ChangeStatus(moved, status, now, events) with { UpdatedAt = now };
                await InsertEventsAsync(c, tx, moved.Id, now, events, cancellationToken).ConfigureAwait(false);
            }

            await c.ExecuteAsync(new CommandDefinition(UpdateItemSql, WorkItemRow.From(moved), tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new Mutation(moved, Changed: true, PreviousStatus: status != current.Status ? current.Status : null);
        }, cancellationToken).ConfigureAwait(false);

        return await CompleteMutationAsync(mutation).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);

        // Deleting a task that is already gone (e.g. from another window) is not an error.
        var projectId = await WriteAsync(async (c, tx) =>
        {
            var owner = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT project_id FROM work_items WHERE id = @Id", new { Id = workItemId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (owner is not null)
            {
                // Links and history go with it (ON DELETE CASCADE).
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_items WHERE id = @Id", new { Id = workItemId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            return owner;
        }, cancellationToken).ConfigureAwait(false);

        if (projectId is not null)
        {
            SafeEvent.Raise(Changed, this, new WorkItemsChangedEventArgs(projectId, workItemId));
        }
    }

    public async Task<WorkItem> AddLinkAsync(string workItemId, WorkItemLinkKind kind, string value, string? label = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        var normalizedValue = WorkItemRules.NormalizeLinkValue(kind, value);
        var normalizedLabel = WorkItemRules.NormalizeLinkLabel(label);

        var mutation = await WriteAsync(async (c, tx) =>
        {
            var current = await LoadAsync(c, tx, workItemId, cancellationToken).ConfigureAwait(false) ?? throw TaskNotFound();
            var comparer = WorkItemRules.LinkValueComparer(kind);
            if (current.Links.Any(l => l.Kind == kind && comparer.Equals(l.Value, normalizedValue)))
            {
                return new Mutation(current, Changed: false, PreviousStatus: null);
            }

            if (current.Links.Count >= WorkItemRules.MaxLinksPerItem)
            {
                throw new ForgeException(ErrorKind.InvalidInput,
                    $"A task can have at most {WorkItemRules.MaxLinksPerItem} links.", "Remove links you no longer need first.");
            }

            var now = Now();
            var link = new WorkItemLink(Ids.New(), kind, normalizedValue, normalizedLabel, now);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO work_item_links (id, work_item_id, kind, value, label, created_at)
                VALUES (@Id, @WorkItemId, @Kind, @Value, @Label, @CreatedAt)
                """,
                new { link.Id, WorkItemId = current.Id, Kind = (int)kind, link.Value, link.Label, link.CreatedAt },
                tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var updated = current with { Links = [.. current.Links, link], UpdatedAt = now };
            await TouchAsync(c, tx, updated, cancellationToken).ConfigureAwait(false);
            await InsertEventsAsync(c, tx, current.Id, now,
                [new PendingEvent(WorkItemEventKind.LinkAdded, "Linked " + WorkItemText.DescribeLink(kind, normalizedValue), null, normalizedValue)],
                cancellationToken).ConfigureAwait(false);
            return new Mutation(updated, Changed: true, PreviousStatus: null);
        }, cancellationToken).ConfigureAwait(false);

        return await CompleteMutationAsync(mutation).ConfigureAwait(false);
    }

    public async Task<WorkItem> RemoveLinkAsync(string workItemId, string linkId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);

        var mutation = await WriteAsync(async (c, tx) =>
        {
            var current = await LoadAsync(c, tx, workItemId, cancellationToken).ConfigureAwait(false) ?? throw TaskNotFound();
            var link = current.Links.FirstOrDefault(l => l.Id == linkId);
            if (link is null)
            {
                return new Mutation(current, Changed: false, PreviousStatus: null);
            }

            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM work_item_links WHERE id = @Id", new { link.Id }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var now = Now();
            var updated = current with { Links = current.Links.Where(l => l.Id != linkId).ToList(), UpdatedAt = now };
            await TouchAsync(c, tx, updated, cancellationToken).ConfigureAwait(false);
            await InsertEventsAsync(c, tx, current.Id, now,
                [new PendingEvent(WorkItemEventKind.LinkRemoved, "Unlinked " + WorkItemText.DescribeLink(link.Kind, link.Value), link.Value, null)],
                cancellationToken).ConfigureAwait(false);
            return new Mutation(updated, Changed: true, PreviousStatus: null);
        }, cancellationToken).ConfigureAwait(false);

        return await CompleteMutationAsync(mutation).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorkItemEvent>> GetHistoryAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        return _database.UseAsync<IReadOnlyList<WorkItemEvent>>(async c =>
        {
            var rows = await c.QueryAsync<EventRow>(new CommandDefinition(
                "SELECT id, at, kind, summary, old_value, new_value FROM work_item_events WHERE work_item_id = @Id ORDER BY at, id",
                new { Id = workItemId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(r => new WorkItemEvent(r.Id, r.At, (WorkItemEventKind)r.Kind, r.Summary, r.OldValue, r.NewValue)).ToList();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WorkItem>> SearchAsync(string projectId, string query, int max = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var text = query?.Trim() ?? string.Empty;
        var limit = Math.Clamp(max, 1, MaxSearchResults);

        return _database.UseAsync<IReadOnlyList<WorkItem>>(async c =>
        {
            CommandDefinition command;
            if (text.Length == 0)
            {
                command = new CommandDefinition(
                    $"SELECT {ItemColumns} FROM work_items WHERE project_id = @ProjectId AND status <> @Done ORDER BY updated_at DESC, number DESC LIMIT @Limit",
                    new { ProjectId = projectId, Done = (int)WorkItemStatus.Done, Limit = limit }, cancellationToken: cancellationToken);
            }
            else
            {
                // Exact key ("#12") first, then title matches, then description/label matches; open items before done ones.
                command = new CommandDefinition(
                    $"""
                    SELECT {ItemColumns} FROM work_items
                    WHERE project_id = @ProjectId
                      AND (number = @Number
                           OR title LIKE @Pattern ESCAPE '\'
                           OR description LIKE @Pattern ESCAPE '\'
                           OR labels LIKE @Pattern ESCAPE '\')
                    ORDER BY CASE WHEN number = @Number THEN 0 WHEN title LIKE @Pattern ESCAPE '\' THEN 1 ELSE 2 END,
                             CASE WHEN status = @Done THEN 1 ELSE 0 END,
                             updated_at DESC,
                             number DESC
                    LIMIT @Limit
                    """,
                    new
                    {
                        ProjectId = projectId,
                        Number = WorkItemRules.TryParseKey(text) ?? -1,
                        Pattern = SqlLike.Contains(text),
                        Done = (int)WorkItemStatus.Done,
                        Limit = limit,
                    },
                    cancellationToken: cancellationToken);
            }

            var rows = (await c.QueryAsync<WorkItemRow>(command).ConfigureAwait(false)).ToList();
            if (rows.Count == 0)
            {
                return [];
            }

            var links = await c.QueryAsync<LinkRow>(new CommandDefinition(
                "SELECT id, work_item_id, kind, value, label, created_at FROM work_item_links WHERE work_item_id IN @Ids ORDER BY created_at, id",
                new { Ids = rows.Select(r => r.Id).ToArray() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return Assemble(rows, links);
        }, cancellationToken);
    }

    private static WorkItem ChangeStatus(WorkItem item, WorkItemStatus status, DateTimeOffset now, List<PendingEvent> events)
    {
        var reopened = item.Status == WorkItemStatus.Done && status != WorkItemStatus.Done;
        var transition = WorkItemText.Transition(reopened ? "Reopened" : "Status",
            WorkItemText.StatusName(item.Status), WorkItemText.StatusName(status));
        events.Add(new PendingEvent(reopened ? WorkItemEventKind.Reopened : WorkItemEventKind.StatusChanged,
            transition, item.Status.ToString(), status.ToString()));
        return item with
        {
            Status = status,
            CompletedAt = status == WorkItemStatus.Done ? now : null,
        };
    }

    private static int ResolveInsertIndex(List<ColumnSlot> column, string? afterId, string? beforeId)
    {
        // Neighbour ids come from a UI that may be slightly stale: unknown ids are ignored.
        if (afterId is not null)
        {
            var after = column.FindIndex(s => s.Id == afterId);
            if (after >= 0)
            {
                return after + 1;
            }
        }

        if (beforeId is not null)
        {
            var before = column.FindIndex(s => s.Id == beforeId);
            if (before >= 0)
            {
                return before;
            }
        }

        return column.Count;
    }

    private static int PositionOf(WorkItem item, List<ColumnSlot> column) =>
        column.Count(s => s.SortOrder < item.SortOrder || (s.SortOrder.Equals(item.SortOrder) && s.Number < item.Number));

    private static async Task<double> RenumberColumnAsync(SqliteConnection c, SqliteTransaction tx, List<ColumnSlot> column, int insertIndex, CancellationToken cancellationToken)
    {
        var updates = column.Select((slot, i) => new
        {
            slot.Id,
            SortOrder = SortOrderMath.Renumber(i < insertIndex ? i : i + 1),
        }).ToList();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE work_items SET sort_order = @SortOrder WHERE id = @Id", updates, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return SortOrderMath.Renumber(insertIndex);
    }

    private static async Task<double> EndOfColumnAsync(SqliteConnection c, SqliteTransaction tx, string projectId, WorkItemStatus status, CancellationToken cancellationToken)
    {
        var last = await c.ExecuteScalarAsync<double?>(new CommandDefinition(
            "SELECT MAX(sort_order) FROM work_items WHERE project_id = @ProjectId AND status = @Status",
            new { ProjectId = projectId, Status = (int)status }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return SortOrderMath.Between(last, null) ?? SortOrderMath.Step;
    }

    private static Task TouchAsync(SqliteConnection c, SqliteTransaction tx, WorkItem item, CancellationToken cancellationToken) =>
        c.ExecuteAsync(new CommandDefinition(
            "UPDATE work_items SET updated_at = @UpdatedAt WHERE id = @Id",
            new { item.Id, item.UpdatedAt }, tx, cancellationToken: cancellationToken));

    private static Task InsertEventsAsync(SqliteConnection c, SqliteTransaction tx, string workItemId, DateTimeOffset at, IEnumerable<PendingEvent> events, CancellationToken cancellationToken) =>
        c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item_events (work_item_id, at, kind, summary, old_value, new_value)
            VALUES (@WorkItemId, @At, @Kind, @Summary, @OldValue, @NewValue)
            """,
            events.Select(e => new { WorkItemId = workItemId, At = at, Kind = (int)e.Kind, e.Summary, e.OldValue, e.NewValue }).ToList(),
            tx, cancellationToken: cancellationToken));

    private static async Task<WorkItem?> LoadAsync(SqliteConnection c, SqliteTransaction? tx, string workItemId, CancellationToken cancellationToken)
    {
        var row = await c.QuerySingleOrDefaultAsync<WorkItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM work_items WHERE id = @Id", new { Id = workItemId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var links = await c.QueryAsync<LinkRow>(new CommandDefinition(
            "SELECT id, work_item_id, kind, value, label, created_at FROM work_item_links WHERE work_item_id = @Id ORDER BY created_at, id",
            new { Id = workItemId }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row.ToItem(links.Select(l => l.ToLink()).ToList());
    }

    private static List<WorkItem> Assemble(IEnumerable<WorkItemRow> rows, IEnumerable<LinkRow> links)
    {
        var linksByItem = links.ToLookup(l => l.WorkItemId, l => l.ToLink(), StringComparer.Ordinal);
        return rows.Select(r => r.ToItem(linksByItem[r.Id].ToList())).ToList();
    }

    private static string? FormatStored(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static ForgeException TaskNotFound() =>
        new(ErrorKind.NotFound, "This task no longer exists.", "It may have been deleted in another window. Refresh the board.");

    // Stored and returned timestamps are UTC so a created item equals the same item read back.
    private DateTimeOffset Now() => _clock.Now.ToUniversalTime();

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _database.UseAsync(async c =>
            {
                // BEGIN IMMEDIATE takes the write lock up front, so two writers can never both read the
                // same MAX(number) or column neighbours before one of them commits.
                using var tx = c.BeginTransaction(deferred: false);
                var result = await work(c, tx).ConfigureAwait(false);
                tx.Commit();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<WorkItem> CompleteMutationAsync(Mutation mutation)
    {
        if (!mutation.Changed)
        {
            return mutation.Item;
        }

        if (mutation.PreviousStatus is { } previous)
        {
            // The change is committed: journal it even if the caller has cancelled meanwhile.
            await _activity.RecordAsync(StatusActivity(mutation.Item, previous), CancellationToken.None).ConfigureAwait(false);
        }

        RaiseChanged(mutation.Item);
        return mutation.Item;
    }

    private static ActivityEntry StatusActivity(WorkItem item, WorkItemStatus previous)
    {
        var (kind, outcome, title) = item.Status switch
        {
            WorkItemStatus.Done => (ActivityKind.WorkItemCompleted, ActivityOutcome.Success, $"Completed task {item.Key}: {item.Title}"),
            _ when previous == WorkItemStatus.Done => (ActivityKind.WorkItemUpdated, ActivityOutcome.Info, $"Reopened task {item.Key}: {item.Title}"),
            _ => (ActivityKind.WorkItemUpdated, ActivityOutcome.Info, $"Moved task {item.Key} to {WorkItemText.StatusName(item.Status)}: {item.Title}"),
        };

        return new ActivityEntry
        {
            ProjectId = item.ProjectId,
            Kind = kind,
            Outcome = outcome,
            Title = title,
            Detail = WorkItemText.Transition("Status", WorkItemText.StatusName(previous), WorkItemText.StatusName(item.Status)),
            RefKind = ActivityRefKind,
            RefValue = item.Id,
        };
    }

    private void RaiseChanged(WorkItem item) =>
        SafeEvent.Raise(Changed, this, new WorkItemsChangedEventArgs(item.ProjectId, item.Id));

    private sealed record Mutation(WorkItem Item, bool Changed, WorkItemStatus? PreviousStatus);

    private sealed record PendingEvent(WorkItemEventKind Kind, string Summary, string? OldValue, string? NewValue);

    private sealed class ColumnSlot
    {
        public string Id { get; set; } = string.Empty;
        public double SortOrder { get; set; }
        public long Number { get; set; }
    }

    private sealed class WorkItemRow
    {
        public string Id { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public long Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long Status { get; set; }
        public long Priority { get; set; }
        public string Labels { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? DueAt { get; set; }
        public double SortOrder { get; set; }

        public static WorkItemRow From(WorkItem item) => new()
        {
            Id = item.Id,
            ProjectId = item.ProjectId,
            Number = item.Number,
            Title = item.Title,
            Description = item.Description,
            Status = (long)item.Status,
            Priority = (long)item.Priority,
            Labels = WorkItemRules.SerializeLabels(item.Labels),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            CompletedAt = item.CompletedAt,
            DueAt = item.DueAt,
            SortOrder = item.SortOrder,
        };

        public WorkItem ToItem(IReadOnlyList<WorkItemLink> links) => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            Number = (int)Number,
            Title = Title,
            Description = Description,
            Status = (WorkItemStatus)Status,
            Priority = (WorkItemPriority)Priority,
            Labels = WorkItemRules.DeserializeLabels(Labels),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            CompletedAt = CompletedAt,
            DueAt = DueAt,
            SortOrder = SortOrder,
            Links = links,
        };
    }

    private sealed class LinkRow
    {
        public string Id { get; set; } = string.Empty;
        public string WorkItemId { get; set; } = string.Empty;
        public long Kind { get; set; }
        public string Value { get; set; } = string.Empty;
        public string? Label { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        public WorkItemLink ToLink() => new(Id, (WorkItemLinkKind)Kind, Value, Label, CreatedAt);
    }

    private sealed class EventRow
    {
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public long Kind { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
    }
}
