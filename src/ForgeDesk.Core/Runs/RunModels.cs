using ForgeDesk.Core.Detection;

namespace ForgeDesk.Core.Runs;

public enum RunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,

    /// <summary>ForgeDesk closed or crashed while the command was running.</summary>
    Interrupted = 5,
}

public sealed record RunRequest
{
    public required string ProjectId { get; init; }
    public required string Label { get; init; }
    public required string CommandLine { get; init; }

    /// <summary>Absolute working directory.</summary>
    public required string WorkingDirectory { get; init; }

    public CommandCategory Category { get; init; }
    public string? CommandId { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Persisted record of a finished (or running) command execution.</summary>
public sealed record RunRecord
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string? CommandId { get; init; }
    public required string Label { get; init; }
    public required string CommandLine { get; init; }
    public required string WorkingDirectory { get; init; }
    public CommandCategory Category { get; init; }
    public RunStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }
    public required string LogPath { get; init; }

    /// <summary>The most relevant error lines extracted from the output.</summary>
    public string? ErrorSummary { get; init; }
    public int LineCount { get; init; }

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
    public bool IsFinished => Status is not RunStatus.Queued and not RunStatus.Running;
}

public sealed record RunLogLine(long Index, DateTimeOffset At, bool IsError, string Text);

public sealed record RunCompletedEventArgs(RunRecord Record);
