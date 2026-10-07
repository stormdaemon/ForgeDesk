using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Commands;

/// <summary>An entry of the Runs list: a group header or a run.</summary>
public abstract class RunListEntry : ObservableObject
{
    public abstract bool IsHeader { get; }
}

/// <summary>"Running 2" / "History" caption of the Runs list.</summary>
public sealed class RunGroupHeader : RunListEntry
{
    public RunGroupHeader(string title) => Title = title;

    public override bool IsHeader => true;

    public string Title { get; }

    public string? CountText { get; set; }

    public override string ToString() => Title;
}

/// <summary>
/// One execution of a command: live (backed by an <see cref="IRunSession"/>) while it runs, then
/// backed by its persisted <see cref="RunRecord"/>. Session events are applied by the owner on
/// the UI thread through <see cref="Refresh"/>.
/// </summary>
public sealed partial class RunItemViewModel : RunListEntry
{
    public RunItemViewModel(IRunSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = session.Id;
        Session = session;
        Request = session.Request;
        Label = session.Request.Label;
        CommandLine = session.Request.CommandLine;
        WorkingDirectory = session.Request.WorkingDirectory;
        CommandId = session.Request.CommandId;
        Category = session.Request.Category;
        StartedAt = session.StartedAt;
        Refresh(now);
    }

    public RunItemViewModel(RunRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        Id = record.Id;
        Label = record.Label;
        CommandLine = record.CommandLine;
        WorkingDirectory = record.WorkingDirectory;
        CommandId = record.CommandId;
        Category = record.Category;
        StartedAt = record.StartedAt;
        Request = new RunRequest
        {
            ProjectId = record.ProjectId,
            Label = record.Label,
            CommandLine = record.CommandLine,
            WorkingDirectory = record.WorkingDirectory,
            Category = record.Category,
            CommandId = record.CommandId,
        };
        Apply(record, now);
    }

    public override bool IsHeader => false;

    public string Id { get; }

    /// <summary>The live session while the run is in progress (null for runs read from history).</summary>
    public IRunSession? Session { get; private set; }

    /// <summary>The persisted record once the run finished (or when read from history).</summary>
    public RunRecord? Record { get; private set; }

    /// <summary>What to start again for "Run again".</summary>
    public RunRequest Request { get; }

    public string Label { get; }

    public string CommandLine { get; }

    public string WorkingDirectory { get; }

    public string? CommandId { get; }

    public CommandCategory Category { get; }

    public DateTimeOffset StartedAt { get; }

    public string StartedAtText => Format.Timestamp(StartedAt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinished), nameof(IsFailed), nameof(StatusText), nameof(StatusIcon), nameof(ToolTip))]
    public partial RunStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExitCodeText), nameof(HasExitCode), nameof(ToolTip))]
    public partial int? ExitCode { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? EndedAt { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressPercent), nameof(ProgressText))]
    public partial double? Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string DurationText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorSummary))]
    public partial string? ErrorSummary { get; private set; }

    [ObservableProperty]
    public partial int LineCount { get; private set; }

    public bool IsRunning => Status is RunStatus.Running or RunStatus.Queued;

    public bool IsFinished => !IsRunning;

    public bool IsFailed => Status == RunStatus.Failed;

    public bool HasProgress => Progress is not null;

    /// <summary>0..100 for a determinate progress bar.</summary>
    public double ProgressPercent => Math.Clamp((Progress ?? 0) * 100, 0, 100);

    public string ProgressText => Progress is { } p ? (p * 100).ToString("0", CultureInfo.CurrentCulture) + " %" : string.Empty;

    public bool HasErrorSummary => !string.IsNullOrWhiteSpace(ErrorSummary);

    public bool HasExitCode => ExitCode is not null;

    public string ExitCodeText => ExitCode is { } code ? $"exit {code.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    public string StatusText => StatusName(Status);

    /// <summary>WPF-UI SymbolRegular name of the status glyph.</summary>
    public string StatusIcon => Status switch
    {
        RunStatus.Succeeded => "CheckmarkCircle16",
        RunStatus.Failed => "DismissCircle16",
        RunStatus.Cancelled => "SubtractCircle16",
        RunStatus.Interrupted => "Warning16",
        _ => "Play16",
    };

    public string ToolTip => $"{Label} · {StatusText}" + (HasExitCode ? $" ({ExitCodeText})" : string.Empty)
        + (DurationText.Length > 0 ? $" · {DurationText}" : string.Empty) + $"\n{CommandLine}";

    public override string ToString() => $"{Label} ({StatusText})";

    public static string StatusName(RunStatus status) => status switch
    {
        RunStatus.Queued => "Starting",
        RunStatus.Running => "Running",
        RunStatus.Succeeded => "Succeeded",
        RunStatus.Failed => "Failed",
        RunStatus.Cancelled => "Cancelled",
        RunStatus.Interrupted => "Interrupted",
        _ => status.ToString(),
    };

    /// <summary>Re-reads the live session (status, progress, duration). Call on the UI thread.</summary>
    public void Refresh(DateTimeOffset now)
    {
        if (Session is { } session && Record is null)
        {
            Status = session.Status;
            ExitCode = session.ExitCode;
            EndedAt = session.EndedAt;
            Progress = session.Progress;
            LineCount = session.LineCount;
        }

        UpdateDuration(now);
    }

    /// <summary>Applies the persisted record (the run finished). Call on the UI thread.</summary>
    public void Apply(RunRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Status = record.Status;
        ExitCode = record.ExitCode;
        EndedAt = record.EndedAt;
        ErrorSummary = record.ErrorSummary;
        LineCount = record.LineCount;
        Progress = null;
        UpdateDuration(now);
    }

    /// <summary>Called every second while visible: keeps the duration of running commands ticking.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (IsRunning)
        {
            Refresh(now);
        }
    }

    private void UpdateDuration(DateTimeOffset now)
    {
        TimeSpan? duration = EndedAt is { } end ? end - StartedAt : IsRunning ? now - StartedAt : null;
        DurationText = duration is { } d ? Format.Duration(d < TimeSpan.Zero ? TimeSpan.Zero : d) : string.Empty;
    }
}
