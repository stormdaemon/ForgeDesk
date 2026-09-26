using Dapper;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Storage;

namespace ForgeDesk.Core.Runs;

/// <summary>Persistence of <see cref="RunRecord"/> rows (table <c>runs</c>).</summary>
internal sealed class RunStore(Database database)
{
    private const string Columns =
        "id, project_id, command_id, label, command_line, working_dir, category, status, started_at, ended_at, exit_code, log_path, error_summary, line_count";

    private static readonly int[] UnfinishedStatuses = [(int)RunStatus.Queued, (int)RunStatus.Running];

    public Task InsertAsync(RunRecord record, CancellationToken cancellationToken) =>
        database.UseAsync(c => c.ExecuteAsync(
            $"""
            INSERT INTO runs ({Columns})
            VALUES (@Id, @ProjectId, @CommandId, @Label, @CommandLine, @WorkingDirectory, @Category, @Status, @StartedAt, @EndedAt, @ExitCode, @LogPath, @ErrorSummary, @LineCount)
            """,
            ToParameters(record)), cancellationToken);

    public Task UpdateAsync(RunRecord record, CancellationToken cancellationToken) =>
        database.UseAsync(c => c.ExecuteAsync(
            """
            UPDATE runs
            SET status = @Status, ended_at = @EndedAt, exit_code = @ExitCode, error_summary = @ErrorSummary, line_count = @LineCount
            WHERE id = @Id
            """,
            ToParameters(record)), cancellationToken);

    public Task<RunRecord?> GetAsync(string id, CancellationToken cancellationToken) =>
        database.UseAsync(async c =>
        {
            var row = await c.QuerySingleOrDefaultAsync<RunRow>($"SELECT {Columns} FROM runs WHERE id = @id", new { id }).ConfigureAwait(false);
            return row?.ToRecord();
        }, cancellationToken);

    public Task<IReadOnlyList<RunRecord>> GetHistoryAsync(string projectId, int limit, CancellationToken cancellationToken) =>
        database.UseAsync<IReadOnlyList<RunRecord>>(async c =>
        {
            var rows = await c.QueryAsync<RunRow>(
                $"SELECT {Columns} FROM runs WHERE project_id = @projectId ORDER BY started_at DESC, id DESC LIMIT @limit",
                new { projectId, limit }).ConfigureAwait(false);
            return rows.Select(r => r.ToRecord()).ToList();
        }, cancellationToken);

    /// <summary>Deletes the project's finished runs and returns their log paths.</summary>
    public Task<IReadOnlyList<string>> DeleteFinishedAsync(string projectId, CancellationToken cancellationToken) =>
        DeleteAsync(
            "SELECT id, log_path FROM runs WHERE project_id = @projectId AND status NOT IN @unfinished",
            new { projectId, unfinished = UnfinishedStatuses },
            cancellationToken);

    /// <summary>Deletes all but the newest <paramref name="keep"/> finished runs of a project.</summary>
    public Task<IReadOnlyList<string>> PruneAsync(string projectId, int keep, CancellationToken cancellationToken) =>
        DeleteAsync(
            """
            SELECT id, log_path FROM runs
            WHERE project_id = @projectId AND status NOT IN @unfinished
            ORDER BY started_at DESC, id DESC
            LIMIT -1 OFFSET @keep
            """,
            new { projectId, keep, unfinished = UnfinishedStatuses },
            cancellationToken);

    /// <summary>Marks rows still Queued/Running (except <paramref name="activeIds"/>) as Interrupted.</summary>
    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string summary, IReadOnlyCollection<string> activeIds, CancellationToken cancellationToken) =>
        database.UseAsync(async c =>
        {
            var stale = (await c.QueryAsync<string>("SELECT id FROM runs WHERE status IN @unfinished", new { unfinished = UnfinishedStatuses })
                .ConfigureAwait(false)).Where(id => !activeIds.Contains(id)).ToList();
            if (stale.Count == 0)
            {
                return 0;
            }

            return await c.ExecuteAsync(
                "UPDATE runs SET status = @status, ended_at = @endedAt, error_summary = @summary WHERE id = @id",
                stale.Select(id => new { id, status = (int)RunStatus.Interrupted, endedAt, summary })).ConfigureAwait(false);
        }, cancellationToken);

    private Task<IReadOnlyList<string>> DeleteAsync(string selectSql, object parameters, CancellationToken cancellationToken) =>
        database.UseAsync<IReadOnlyList<string>>(async c =>
        {
            await using var transaction = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var rows = (await c.QueryAsync<(string Id, string LogPath)>(selectSql, parameters, transaction).ConfigureAwait(false)).ToList();
            if (rows.Count > 0)
            {
                await c.ExecuteAsync("DELETE FROM runs WHERE id = @Id", rows.Select(r => new { r.Id }), transaction).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(r => r.LogPath).ToList();
        }, cancellationToken);

    private static object ToParameters(RunRecord r) => new
    {
        r.Id,
        r.ProjectId,
        r.CommandId,
        r.Label,
        r.CommandLine,
        r.WorkingDirectory,
        Category = (int)r.Category,
        Status = (int)r.Status,
        r.StartedAt,
        r.EndedAt,
        r.ExitCode,
        r.LogPath,
        r.ErrorSummary,
        r.LineCount,
    };

    private sealed class RunRow
    {
        public string Id { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string? CommandId { get; set; }
        public string Label { get; set; } = string.Empty;
        public string CommandLine { get; set; } = string.Empty;
        public string WorkingDir { get; set; } = string.Empty;
        public long Category { get; set; }
        public long Status { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public long? ExitCode { get; set; }
        public string LogPath { get; set; } = string.Empty;
        public string? ErrorSummary { get; set; }
        public long LineCount { get; set; }

        public RunRecord ToRecord() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            CommandId = CommandId,
            Label = Label,
            CommandLine = CommandLine,
            WorkingDirectory = WorkingDir,
            Category = Enum.IsDefined((CommandCategory)Category) ? (CommandCategory)Category : CommandCategory.Other,
            Status = Enum.IsDefined((RunStatus)Status) ? (RunStatus)Status : RunStatus.Interrupted,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            ExitCode = ExitCode is { } code ? (int)code : null,
            LogPath = LogPath,
            ErrorSummary = ErrorSummary,
            LineCount = (int)Math.Min(LineCount, int.MaxValue),
        };
    }
}
