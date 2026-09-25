using ForgeDesk.Core.GitHub;

namespace ForgeDesk.Core.Projects;

public enum AttentionLevel
{
    None = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>A reason a project needs the developer's attention, shown on the dashboard.</summary>
public sealed record AttentionReason(AttentionLevel Level, string Message, string? Section = null);

/// <summary>
/// Cheap-to-compute state of a project for the dashboard and the workspace header.
/// Persisted so the dashboard renders instantly on startup, then refreshed in the background.
/// </summary>
public sealed record ProjectSnapshot
{
    public required string ProjectId { get; init; }
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>False when the folder was moved or deleted.</summary>
    public bool FolderExists { get; init; } = true;

    public bool IsGitRepository { get; init; }
    public string? Branch { get; init; }
    public bool IsDetachedHead { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int ChangedFiles { get; init; }
    public int StagedFiles { get; init; }
    public int UntrackedFiles { get; init; }
    public int ConflictedFiles { get; init; }
    public int StashCount { get; init; }
    public DateTimeOffset? LastCommitAt { get; init; }
    public string? LastCommitSubject { get; init; }

    public string? PrimaryLanguage { get; init; }
    public IReadOnlyList<string> Technologies { get; init; } = [];

    public CiSummary? Ci { get; init; }
    public int OpenWorkItems { get; init; }
    public int RunningCommands { get; init; }
    public RunOutcomeSummary? LastRun { get; init; }

    public IReadOnlyList<AttentionReason> Attention { get; init; } = [];

    public AttentionLevel AttentionLevel => Attention.Count == 0 ? AttentionLevel.None : Attention.Max(a => a.Level);

    /// <summary>Error encountered while computing the snapshot (e.g. git missing), shown inline.</summary>
    public string? Problem { get; init; }
}

public sealed record RunOutcomeSummary(string Label, bool Succeeded, DateTimeOffset At);
