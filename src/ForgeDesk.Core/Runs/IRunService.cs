namespace ForgeDesk.Core.Runs;

/// <summary>
/// A live command execution. Output is buffered (bounded) in memory and streamed to a log
/// file on disk. Events fire on thread-pool threads: marshal to the UI thread.
/// </summary>
public interface IRunSession
{
    string Id { get; }
    RunRequest Request { get; }
    RunStatus Status { get; }
    DateTimeOffset StartedAt { get; }
    DateTimeOffset? EndedAt { get; }
    int? ExitCode { get; }

    /// <summary>0..1 when a progress indicator was recognized in the output, otherwise null.</summary>
    double? Progress { get; }

    /// <summary>Time of the last output line (used to flag stalled processes).</summary>
    DateTimeOffset LastOutputAt { get; }

    int LineCount { get; }

    /// <summary>Returns the buffered lines from <paramref name="fromIndex"/> (older lines may have been trimmed; read the log file for all).</summary>
    IReadOnlyList<RunLogLine> GetLines(long fromIndex = 0);

    event EventHandler<RunLogLine>? LineReceived;
    event EventHandler? StatusChanged;
    event EventHandler? ProgressChanged;

    /// <summary>Completes when the process has exited and the record is persisted.</summary>
    Task<RunRecord> Completion { get; }
}

public interface IRunService
{
    event EventHandler<IRunSession>? RunStarted;
    event EventHandler<RunCompletedEventArgs>? RunCompleted;

    IReadOnlyList<IRunSession> ActiveRuns { get; }

    IRunSession? FindActive(string runId);

    Task<IRunSession> StartAsync(RunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Kills the run's whole process tree and marks it Cancelled.</summary>
    Task CancelAsync(string runId);

    Task CancelAllAsync();

    Task<IReadOnlyList<RunRecord>> GetHistoryAsync(string projectId, int limit = 100, CancellationToken cancellationToken = default);

    Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Reads a run's full log from disk (bounded to <paramref name="maxLines"/> tail lines).</summary>
    Task<IReadOnlyList<RunLogLine>> ReadLogAsync(string runId, int maxLines = 20000, CancellationToken cancellationToken = default);

    Task DeleteHistoryAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Marks runs left "Running" by a previous session as Interrupted. Called at startup.</summary>
    Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default);
}
