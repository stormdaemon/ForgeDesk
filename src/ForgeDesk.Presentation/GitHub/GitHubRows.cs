using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>A GitHub label as a colored pill: a dot in the label's color on a tint of it.</summary>
public sealed class LabelViewModel
{
    private const string Fallback = "8B949E";

    public LabelViewModel(GitHubLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        Name = label.Name;
        var hex = Normalize(label.Color);
        Color = "#" + hex;
        Background = "#33" + hex;
        Border = "#66" + hex;
    }

    public string Name { get; }

    /// <summary>"#RRGGBB".</summary>
    public string Color { get; }

    /// <summary>The label color at 20 % opacity ("#33RRGGBB"), readable under primary text in both themes.</summary>
    public string Background { get; }

    /// <summary>The label color at 40 % opacity.</summary>
    public string Border { get; }

    public static IReadOnlyList<LabelViewModel> From(IEnumerable<GitHubLabel>? labels) =>
        labels?.Select(l => new LabelViewModel(l)).ToList() ?? [];

    private static string Normalize(string? color)
    {
        var value = color?.Trim().TrimStart('#') ?? string.Empty;
        return value.Length == 6 && value.All(char.IsAsciiHexDigit) ? value.ToUpperInvariant() : Fallback;
    }
}

/// <summary>Labels and formatting shared by the GitHub views.</summary>
public static class GitHubText
{
    public static string RunState(CiState state, string? conclusion = null) => state switch
    {
        CiState.Queued => "Queued",
        CiState.Running => "In progress",
        CiState.Success => "Succeeded",
        CiState.Failure => string.Equals(conclusion, "timed_out", StringComparison.OrdinalIgnoreCase) ? "Timed out" : "Failed",
        CiState.Cancelled => "Cancelled",
        CiState.None => "Skipped",
        _ => "Unknown",
    };

    public static StatusTone RunTone(CiState state) => state switch
    {
        CiState.Queued => StatusTone.Warning,
        CiState.Running => StatusTone.Running,
        CiState.Success => StatusTone.Success,
        CiState.Failure => StatusTone.Danger,
        _ => StatusTone.Neutral,
    };

    /// <summary>WPF-UI symbol for a run, job or step state.</summary>
    public static string RunIcon(CiState state) => state switch
    {
        CiState.Queued => "Clock16",
        CiState.Running => "ArrowSync16",
        CiState.Success => "CheckmarkCircle16",
        CiState.Failure => "DismissCircle16",
        CiState.Cancelled => "SubtractCircle16",
        _ => "Circle16",
    };

    public static string Event(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => "Unknown",
        "push" => "Push",
        "pull_request" or "pull_request_target" => "Pull request",
        "workflow_dispatch" => "Manual",
        "schedule" => "Scheduled",
        "release" => "Release",
        "workflow_run" => "Workflow run",
        "repository_dispatch" => "API",
        "merge_group" => "Merge queue",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.Replace('_', ' ')),
    };

    public static string PullRequestState(PullRequestState state) => state switch
    {
        Core.GitHub.PullRequestState.Draft => "Draft",
        Core.GitHub.PullRequestState.Merged => "Merged",
        Core.GitHub.PullRequestState.Closed => "Closed",
        _ => "Open",
    };

    public static StatusTone PullRequestTone(PullRequestState state) => state switch
    {
        Core.GitHub.PullRequestState.Draft => StatusTone.Neutral,
        Core.GitHub.PullRequestState.Merged => StatusTone.Info,
        Core.GitHub.PullRequestState.Closed => StatusTone.Danger,
        _ => StatusTone.Success,
    };

    /// <summary>"12" or "100+" when a list was capped at <paramref name="cap"/>.</summary>
    public static string CappedCount(int count, int cap) =>
        count >= cap ? $"{cap.ToString("N0", CultureInfo.CurrentCulture)}+" : count.ToString("N0", CultureInfo.CurrentCulture);

    public static string ShortSha(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? string.Empty;

    /// <summary>Duration between two instants, or null when one is missing.</summary>
    public static TimeSpan? Between(DateTimeOffset? start, DateTimeOffset? end) =>
        start is { } s && end is { } e && e >= s ? e - s : null;
}

/// <summary>A pull request in the list. Updated in place when its details are loaded (checks, state).</summary>
public sealed partial class PullRequestRowViewModel : ObservableObject
{
    public PullRequestRowViewModel(GitHubPullRequest pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        PullRequest = pullRequest;
        Labels = LabelViewModel.From(pullRequest.Labels);
    }

    public GitHubPullRequest PullRequest { get; private set; }

    public int Number => PullRequest.Number;

    public string NumberText => $"#{Number}";

    public string Title => PullRequest.Title;

    public string Author => PullRequest.Author;

    public string HeadBranch => PullRequest.HeadBranch;

    public string BaseBranch => PullRequest.BaseBranch;

    public PullRequestState State => PullRequest.State;

    public string StateText => GitHubText.PullRequestState(State);

    public StatusTone StateTone => GitHubText.PullRequestTone(State);

    public bool IsOpen => State is PullRequestState.Open or PullRequestState.Draft;

    public CiState Checks => PullRequest.Checks;

    /// <summary>Checks are only known once the details were loaded (the list endpoint does not include them).</summary>
    public bool HasChecks => Checks is not CiState.Unknown and not CiState.None;

    public DateTimeOffset UpdatedAt => PullRequest.UpdatedAt ?? PullRequest.CreatedAt;

    public int Comments => PullRequest.Comments;

    public IReadOnlyList<LabelViewModel> Labels { get; private set; }

    public bool HasLabels => Labels.Count > 0;

    public string HtmlUrl => PullRequest.HtmlUrl;

    public string ToolTip => $"{NumberText} {Title}\n{Author} wants to merge {HeadBranch} into {BaseBranch}";

    internal bool Matches(string search) =>
        search.Length == 0
        || Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Author.Contains(search, StringComparison.OrdinalIgnoreCase)
        || HeadBranch.Contains(search, StringComparison.OrdinalIgnoreCase)
        || NumberText.Equals(search, StringComparison.OrdinalIgnoreCase)
        || Number.ToString(CultureInfo.InvariantCulture) == search
        || Labels.Any(l => l.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    internal void Update(GitHubPullRequest pullRequest)
    {
        // The list endpoint never carries checks: keep the ones a detail call found.
        PullRequest = pullRequest.Checks == CiState.Unknown && PullRequest.Checks != CiState.Unknown
            ? pullRequest with { Checks = PullRequest.Checks }
            : pullRequest;
        Labels = LabelViewModel.From(pullRequest.Labels);
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>An issue in the list.</summary>
public sealed partial class IssueRowViewModel : ObservableObject
{
    public IssueRowViewModel(GitHubIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        Issue = issue;
        Labels = LabelViewModel.From(issue.Labels);
    }

    public GitHubIssue Issue { get; private set; }

    public int Number => Issue.Number;

    public string NumberText => $"#{Number}";

    public string Title => Issue.Title;

    public string Author => Issue.Author;

    public bool IsOpen => Issue.IsOpen;

    public string StateText => IsOpen ? "Open" : "Closed";

    public StatusTone StateTone => IsOpen ? StatusTone.Success : StatusTone.Info;

    /// <summary>WPF-UI symbol: a ringed dot while open, a check once closed.</summary>
    public string StateIcon => IsOpen ? "Record16" : "CheckmarkCircle16";

    public DateTimeOffset UpdatedAt => Issue.UpdatedAt ?? Issue.CreatedAt;

    public int Comments => Issue.Comments;

    public IReadOnlyList<LabelViewModel> Labels { get; private set; }

    public bool HasLabels => Labels.Count > 0;

    public string HtmlUrl => Issue.HtmlUrl;

    public string ToolTip => $"{NumberText} {Title}\nOpened by {Author}";

    internal bool Matches(string search) =>
        search.Length == 0
        || Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Author.Contains(search, StringComparison.OrdinalIgnoreCase)
        || NumberText.Equals(search, StringComparison.OrdinalIgnoreCase)
        || Number.ToString(CultureInfo.InvariantCulture) == search
        || Labels.Any(l => l.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    internal void Update(GitHubIssue issue)
    {
        Issue = issue;
        Labels = LabelViewModel.From(issue.Labels);
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A workflow run (Actions list, CI cards). Updated in place by the auto-refresh so selection survives.</summary>
public sealed partial class WorkflowRunRowViewModel : ObservableObject
{
    public WorkflowRunRowViewModel(WorkflowRunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Run = run;
    }

    public WorkflowRunInfo Run { get; private set; }

    public long Id => Run.Id;

    public string Name => Run.Name;

    public string RunNumberText => Run.RunAttempt > 1 ? $"#{Run.RunNumber} · attempt {Run.RunAttempt}" : $"#{Run.RunNumber}";

    public string EventText => GitHubText.Event(Run.Event);

    public string? Branch => Run.Branch;

    public string ShortSha => GitHubText.ShortSha(Run.HeadSha);

    public string CommitMessage => string.IsNullOrWhiteSpace(Run.CommitMessage) ? Run.Name : Run.CommitMessage;

    public string? Actor => Run.Actor;

    public CiState State => Run.State;

    public string StateText => GitHubText.RunState(Run.State, Run.Conclusion);

    public StatusTone StateTone => GitHubText.RunTone(Run.State);

    public string StateIcon => GitHubText.RunIcon(Run.State);

    public bool IsInProgress => Run.State is CiState.Queued or CiState.Running;

    public bool IsFailed => Run.State == CiState.Failure;

    /// <summary>A finished run can be re-run; one still queued or running can only be cancelled.</summary>
    public bool CanRerun => !IsInProgress;

    public bool CanRerunFailed => IsFailed;

    public bool CanCancel => IsInProgress;

    public DateTimeOffset CreatedAt => Run.CreatedAt;

    public TimeSpan? Duration => Run.Duration;

    public string DurationText => IsInProgress ? "—" : Format.Duration(Duration);

    public string HtmlUrl => Run.HtmlUrl;

    public string ToolTip => $"{Name} {RunNumberText} · {StateText}\n{CommitMessage}";

    internal void Update(WorkflowRunInfo run)
    {
        if (run == Run)
        {
            return;
        }

        Run = run;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A step of a job in the run details.</summary>
public sealed class WorkflowStepViewModel
{
    public WorkflowStepViewModel(WorkflowStepInfo step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Number = step.Number;
        Name = step.Name;
        State = step.State;
        Duration = GitHubText.Between(step.StartedAt, step.CompletedAt);
    }

    public int Number { get; }

    public string Name { get; }

    public CiState State { get; }

    public string StateIcon => GitHubText.RunIcon(State);

    public StatusTone StateTone => GitHubText.RunTone(State);

    public string StateText => GitHubText.RunState(State);

    public TimeSpan? Duration { get; }

    public string DurationText => Duration is null ? string.Empty : Format.Duration(Duration);
}

/// <summary>A job of a workflow run, with its steps.</summary>
public sealed partial class WorkflowJobViewModel : ObservableObject
{
    public WorkflowJobViewModel(WorkflowJobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Id = job.Id;
        Name = job.Name;
        State = job.State;
        HtmlUrl = job.HtmlUrl;
        Duration = GitHubText.Between(job.StartedAt, job.CompletedAt);
        Steps = job.Steps.Select(s => new WorkflowStepViewModel(s)).ToList();
        IsExpanded = job.State is CiState.Failure or CiState.Running;
    }

    public long Id { get; }

    public string Name { get; }

    public CiState State { get; }

    public string StateIcon => GitHubText.RunIcon(State);

    public StatusTone StateTone => GitHubText.RunTone(State);

    public string StateText => GitHubText.RunState(State);

    public string HtmlUrl { get; }

    public TimeSpan? Duration { get; }

    public string DurationText => State is CiState.Running or CiState.Queued ? StateText : Format.Duration(Duration);

    public IReadOnlyList<WorkflowStepViewModel> Steps { get; }

    public bool HasSteps => Steps.Count > 0;

    /// <summary>Failed and running jobs open expanded, so the step that matters is visible at once.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>A workflow in the Actions filter list; <see cref="Id"/> is null for "All workflows".</summary>
public sealed class WorkflowFilterViewModel
{
    public WorkflowFilterViewModel(long? id, string name, string? path, string? htmlUrl, bool isActive = true)
    {
        Id = id;
        Name = name;
        Path = path;
        HtmlUrl = htmlUrl;
        IsActive = isActive;
    }

    public long? Id { get; }

    public string Name { get; }

    public string? Path { get; }

    public string? HtmlUrl { get; }

    /// <summary>False for disabled workflows (they keep their past runs).</summary>
    public bool IsActive { get; }

    public bool IsAll => Id is null;

    public string Icon => IsAll ? "Apps16" : "Flow16";

    public string ToolTip => IsAll ? "Runs of every workflow" : IsActive ? Path ?? Name : $"{Path ?? Name} (disabled)";
}
