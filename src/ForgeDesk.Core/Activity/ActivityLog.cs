using System.Text;
using Dapper;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Activity;

/// <summary>SQLite-backed <see cref="IActivityLog"/> (table <c>activity</c>).</summary>
internal sealed class ActivityLog : IActivityLog
{
    internal const int MaxTitleLength = 300;
    internal const int MaxDetailLength = 8000;
    internal const int MaxRefKindLength = 64;
    internal const int MaxRefValueLength = 2048;
    internal const int MaxQueryLimit = 1000;

    private const string Columns = "id, project_id, at, kind, outcome, title, detail, ref_kind, ref_value";

    private readonly Database _database;
    private readonly IClock _clock;
    private readonly ILogger<ActivityLog> _logger;

    public ActivityLog(Database database, IClock clock, ILogger<ActivityLog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
        _logger = logger ?? NullLogger<ActivityLog>.Instance;
    }

    public event EventHandler<ActivityEntry>? EntryAdded;

    public async Task<ActivityEntry?> RecordAsync(ActivityEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            var normalized = Normalize(entry);
            var id = await _database.UseAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO activity (project_id, at, kind, outcome, title, detail, ref_kind, ref_value)
                VALUES (@ProjectId, @At, @Kind, @Outcome, @Title, @Detail, @RefKind, @RefValue);
                SELECT last_insert_rowid();
                """,
                new
                {
                    normalized.ProjectId,
                    normalized.At,
                    Kind = (int)normalized.Kind,
                    Outcome = (int)normalized.Outcome,
                    normalized.Title,
                    normalized.Detail,
                    normalized.RefKind,
                    normalized.RefValue,
                },
                cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);

            var saved = normalized with { Id = id };
            SafeEvent.Raise(EntryAdded, this, saved);
            return saved;
        }
        catch (Exception ex)
        {
            // Journaling must never break the operation it describes (e.g. a project removed meanwhile).
            _logger.LogWarning(ex, "Could not record activity entry {Kind} for project {ProjectId}", entry.Kind, entry.ProjectId);
            return null;
        }
    }

    public async Task<IReadOnlyList<ActivityEntry>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = new StringBuilder($"SELECT {Columns} FROM activity WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (query.ProjectId is not null)
        {
            sql.Append(" AND project_id = @ProjectId");
            parameters.Add("ProjectId", query.ProjectId);
        }

        if (query.Kinds is { Count: > 0 } kinds)
        {
            sql.Append(" AND kind IN @Kinds");
            parameters.Add("Kinds", kinds.Select(k => (int)k).Distinct().ToArray());
        }

        if (query.Outcome is { } outcome)
        {
            sql.Append(" AND outcome = @Outcome");
            parameters.Add("Outcome", (int)outcome);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append(@" AND (title LIKE @Search ESCAPE '\' OR detail LIKE @Search ESCAPE '\')");
            parameters.Add("Search", SqlLike.Contains(query.Search.Trim()));
        }

        if (query.Before is { } before)
        {
            // Keyset pagination: (at, id) is unique and matches the ORDER BY, so pages never overlap or skip rows.
            sql.Append(query.BeforeId is not null
                ? " AND (at < @Before OR (at = @Before AND id < @BeforeId))"
                : " AND at < @Before");
            parameters.Add("Before", before);
            if (query.BeforeId is { } beforeId)
            {
                parameters.Add("BeforeId", beforeId);
            }
        }

        sql.Append(" ORDER BY at DESC, id DESC LIMIT @Limit");
        parameters.Add("Limit", Math.Clamp(query.Limit, 1, MaxQueryLimit));

        var rows = await _database.UseAsync(c => c.QueryAsync<ActivityRow>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public Task ClearAsync(string? projectId, CancellationToken cancellationToken = default) =>
        _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            projectId is null ? "DELETE FROM activity" : "DELETE FROM activity WHERE project_id = @ProjectId",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken)), cancellationToken);

    public Task PruneAsync(int maxEntriesPerProject = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntriesPerProject);

        // PARTITION BY puts every NULL project_id in one partition, so global entries get their own quota.
        return _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM activity WHERE id IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY project_id ORDER BY at DESC, id DESC) AS position
                    FROM activity)
                WHERE position > @Max)
            """,
            new { Max = maxEntriesPerProject },
            cancellationToken: cancellationToken)), cancellationToken);
    }

    private ActivityEntry Normalize(ActivityEntry entry)
    {
        var title = Clip(entry.Title?.Trim(), MaxTitleLength);
        return entry with
        {
            ProjectId = string.IsNullOrWhiteSpace(entry.ProjectId) ? null : entry.ProjectId,
            At = entry.At == default ? _clock.Now : entry.At,
            Title = string.IsNullOrEmpty(title) ? entry.Kind.ToString() : title,
            Detail = Clip(entry.Detail?.Trim(), MaxDetailLength),
            RefKind = Clip(entry.RefKind?.Trim(), MaxRefKindLength),
            RefValue = Clip(entry.RefValue?.Trim(), MaxRefValueLength),
        };
    }

    private static string? Clip(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }

    private sealed class ActivityRow
    {
        public long Id { get; set; }
        public string? ProjectId { get; set; }
        public DateTimeOffset At { get; set; }
        public long Kind { get; set; }
        public long Outcome { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public string? RefKind { get; set; }
        public string? RefValue { get; set; }

        public ActivityEntry ToEntry() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            At = At,
            Kind = (ActivityKind)Kind,
            Outcome = (ActivityOutcome)Outcome,
            Title = Title,
            Detail = Detail,
            RefKind = RefKind,
            RefValue = RefValue,
        };
    }
}
