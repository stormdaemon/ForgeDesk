using ForgeDesk.Core.Activity;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Controls;

/// <summary>Semantic status used by <see cref="StatusDot"/>, <see cref="Pill"/> and the StatusToBrush converter.</summary>
public enum StatusKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
    Running,
}

/// <summary>Maps domain states (run status, CI state, severities…) to a <see cref="StatusKind"/>.</summary>
public static class StatusKinds
{
    /// <summary>Returns the status for a known value; anything else is <see cref="StatusKind.Neutral"/>.</summary>
    public static StatusKind From(object? value) => value switch
    {
        StatusKind kind => kind,
        RunStatus run => From(run),
        CiState ci => From(ci),
        NotificationSeverity severity => From(severity),
        ActivityOutcome outcome => From(outcome),
        AttentionLevel attention => From(attention),
        string text => FromName(text),
        _ => StatusKind.Neutral,
    };

    public static StatusKind From(RunStatus status) => status switch
    {
        RunStatus.Running => StatusKind.Running,
        RunStatus.Succeeded => StatusKind.Success,
        RunStatus.Failed => StatusKind.Danger,
        RunStatus.Interrupted => StatusKind.Warning,
        _ => StatusKind.Neutral,
    };

    public static StatusKind From(CiState state) => state switch
    {
        CiState.Running => StatusKind.Running,
        CiState.Queued => StatusKind.Warning,
        CiState.Success => StatusKind.Success,
        CiState.Failure => StatusKind.Danger,
        _ => StatusKind.Neutral,
    };

    public static StatusKind From(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => StatusKind.Success,
        NotificationSeverity.Warning => StatusKind.Warning,
        NotificationSeverity.Error => StatusKind.Danger,
        _ => StatusKind.Info,
    };

    public static StatusKind From(ActivityOutcome outcome) => outcome switch
    {
        ActivityOutcome.Success => StatusKind.Success,
        ActivityOutcome.Warning => StatusKind.Warning,
        ActivityOutcome.Failure => StatusKind.Danger,
        _ => StatusKind.Info,
    };

    public static StatusKind From(AttentionLevel level) => level switch
    {
        AttentionLevel.Info => StatusKind.Info,
        AttentionLevel.Warning => StatusKind.Warning,
        AttentionLevel.Critical => StatusKind.Danger,
        _ => StatusKind.Neutral,
    };

    /// <summary>Parses a status name ("Success", "danger", "error"…), case-insensitively.</summary>
    public static StatusKind FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StatusKind.Neutral;
        }

        if (Enum.TryParse<StatusKind>(name.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind))
        {
            return kind;
        }

        return name.Trim().ToUpperInvariant() switch
        {
            "ERROR" or "FAILED" or "FAILURE" or "CRITICAL" => StatusKind.Danger,
            "OK" or "PASSED" or "SUCCEEDED" or "DONE" => StatusKind.Success,
            "PENDING" or "QUEUED" or "STALLED" => StatusKind.Warning,
            _ => StatusKind.Neutral,
        };
    }
}
