using System.Collections.Concurrent;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Starts commands through the platform shell, tracks them while they run and keeps their
/// history (database rows + log files under <see cref="IAppPaths.RunLogsDirectory"/>).
/// </summary>
internal sealed class RunService : IRunService
{
    internal const string InterruptedSummary = "ForgeDesk was closed while this command was running.";

    private static readonly TimeSpan CancelWaitTimeout = TimeSpan.FromSeconds(15);

    private readonly RunStore _store;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IActivityLog _activityLog;
    private readonly IClock _clock;
    private readonly ILogger<RunService> _logger;
    private readonly ConcurrentDictionary<string, RunSession> _active = new(StringComparer.Ordinal);

    public RunService(
        Database database,
        IAppPaths paths,
        ISettingsService settings,
        IActivityLog activityLog,
        IClock clock,
        ILogger<RunService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _store = new RunStore(database);
        _paths = paths;
        _settings = settings;
        _activityLog = activityLog;
        _clock = clock;
        _logger = logger ?? NullLogger<RunService>.Instance;
    }

    public event EventHandler<IRunSession>? RunStarted;

    public event EventHandler<RunCompletedEventArgs>? RunCompleted;

    /// <summary>Lines kept in memory per run.</summary>
    internal int BufferCapacity { get; init; } = RunLogBuffer.DefaultCapacity;

    internal TimeSpan LogFlushInterval { get; init; } = RunLogWriter.DefaultFlushInterval;

    /// <summary>How long to keep reading output after the process exited (grandchildren may hold the pipes).</summary>
    internal TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public IReadOnlyList<IRunSession> ActiveRuns => _active.Values.OrderBy(s => s.StartedAt).ToList<IRunSession>();

    public IRunSession? FindActive(string runId) =>
        runId is not null && _active.TryGetValue(runId, out var session) ? session : null;

    public async Task<IRunSession> StartAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var id = Ids.New();
        var startedAt = _clock.Now;
        var logPath = Path.Combine(_paths.RunLogsDirectory, request.ProjectId, id + ".log");
        await _store.InsertAsync(new RunRecord
        {
            Id = id,
            ProjectId = request.ProjectId,
            CommandId = request.CommandId,
            Label = request.Label,
            CommandLine = request.CommandLine,
            WorkingDirectory = request.WorkingDirectory,
            Category = request.Category,
            Status = RunStatus.Running,
            StartedAt = startedAt,
            LogPath = logPath,
        }, cancellationToken).ConfigureAwait(false);

        var log = RunLogWriter.Open(logPath, _logger, LogFlushInterval);
        var session = new RunSession(id, request, startedAt, log, _clock, _logger, OnSessionFinishedAsync, BufferCapacity, OutputDrainTimeout);
        _active[id] = session;
        SafeEvent.Raise(RunStarted, this, (IRunSession)session);

        var spec = ShellCommand.Create(request.CommandLine, request.WorkingDirectory, RunEnvironment.Build(request.Environment));
        session.Start(spec);
        // The command line itself is not logged: users sometimes pass tokens as arguments.
        _logger.LogInformation("Run {RunId} started ({Label})", id, request.Label);
        return session;
    }

    public async Task CancelAsync(string runId)
    {
        if (runId is null || !_active.TryGetValue(runId, out var session))
        {
            return;
        }

        session.Cancel();
        try
        {
            await session.Completion.WaitAsync(CancelWaitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Run {RunId} did not stop within {Timeout}", runId, CancelWaitTimeout);
        }
    }

    public Task CancelAllAsync() => Task.WhenAll(_active.Keys.ToList().Select(CancelAsync));

    public Task<IReadOnlyList<RunRecord>> GetHistoryAsync(string projectId, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _store.GetHistoryAsync(projectId, Math.Clamp(limit, 1, 10_000), cancellationToken);
    }

    public async Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_active.TryGetValue(runId, out var session))
        {
            return session.ToRecord();
        }

        return await _store.GetAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunLogLine>> ReadLogAsync(string runId, int maxLines = 20000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        string path;
        if (_active.TryGetValue(runId, out var session))
        {
            session.FlushLog();
            path = session.LogPath;
        }
        else
        {
            var record = await _store.GetAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw ForgeException.NotFound("This run");
            path = ResolveLogPath(record);
        }

        try
        {
            return await RunLogFormat.ReadTailAsync(path, maxLines, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ForgeException(ErrorKind.PermissionDenied, "The log of this run could not be opened.",
                "Check the permissions of ForgeDesk's data folder.", ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new ForgeException(ErrorKind.StorageFailure, "The log of this run could not be read.",
                "The file may be in use by another program. Try again in a moment.", ex.Message, ex);
        }
    }

    public async Task DeleteHistoryAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var logPaths = await _store.DeleteFinishedAsync(projectId, cancellationToken).ConfigureAwait(false);
        DeleteLogFiles(logPaths);
        TryDeleteEmptyDirectory(Path.Combine(_paths.RunLogsDirectory, projectId));
    }

    public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default) =>
        _store.MarkInterruptedAsync(_clock.Now, InterruptedSummary, _active.Keys.ToList(), cancellationToken);

    private static void Validate(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CommandLine))
        {
            throw ForgeException.InvalidInput("The command is empty.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            throw ForgeException.InvalidInput("The command needs an absolute working directory.");
        }

        // The project id names the log folder: it must be a plain, single path segment.
        if (string.IsNullOrWhiteSpace(request.ProjectId)
            || request.ProjectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || request.ProjectId.IndexOfAny(['/', '\\', ':']) >= 0
            || request.ProjectId is "." or "..")
        {
            throw ForgeException.InvalidInput("The project of this command is not valid.");
        }

        if (string.IsNullOrWhiteSpace(request.Label))
        {
            throw ForgeException.InvalidInput("The command needs a name.");
        }
    }

    private async Task OnSessionFinishedAsync(RunSession session, RunRecord record)
    {
        try
        {
            await _store.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId}: could not save its result", record.Id);
        }

        _active.TryRemove(session.Id, out _);
        _logger.LogInformation("Run {RunId} finished: {Status} (exit {ExitCode}, {Lines} lines)", record.Id, record.Status, record.ExitCode, record.LineCount);

        await PruneAsync(record.ProjectId).ConfigureAwait(false);
        await RecordActivityAsync(record).ConfigureAwait(false);
        SafeEvent.Raise(RunCompleted, this, new RunCompletedEventArgs(record));
    }

    private async Task PruneAsync(string projectId)
    {
        try
        {
            var keep = Math.Max(1, _settings.Current.RunHistoryPerProject);
            var logPaths = await _store.PruneAsync(projectId, keep, CancellationToken.None).ConfigureAwait(false);
            DeleteLogFiles(logPaths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prune the run history of project {ProjectId}", projectId);
        }
    }

    private async Task RecordActivityAsync(RunRecord record)
    {
        try
        {
            await _activityLog.RecordAsync(new ActivityEntry
            {
                ProjectId = record.ProjectId,
                At = record.EndedAt ?? _clock.Now,
                Kind = ActivityKind.RunCompleted,
                Outcome = record.Status switch
                {
                    RunStatus.Succeeded => ActivityOutcome.Success,
                    RunStatus.Failed => ActivityOutcome.Failure,
                    RunStatus.Cancelled or RunStatus.Interrupted => ActivityOutcome.Warning,
                    _ => ActivityOutcome.Info,
                },
                Title = RunTitles.Completed(record),
                Detail = record.ErrorSummary,
                RefKind = "run",
                RefValue = record.Id,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not journal run {RunId}", record.Id);
        }
    }

    private string ResolveLogPath(RunRecord record)
    {
        if (File.Exists(record.LogPath))
        {
            return record.LogPath;
        }

        // The data folder may have moved (FORGEDESK_DATA_DIR, restored backup): look where it would be now.
        var current = Path.Combine(_paths.RunLogsDirectory, record.ProjectId, record.Id + ".log");
        return File.Exists(current) ? current : record.LogPath;
    }

    private void DeleteLogFiles(IEnumerable<string> logPaths)
    {
        foreach (var path in logPaths)
        {
            // Paths come from the database: never delete anything outside the run logs folder.
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !PathUtil.IsWithin(_paths.RunLogsDirectory, path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete run log {Path}", path);
            }
        }
    }

    private void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove {Directory}", directory);
        }
    }
}
