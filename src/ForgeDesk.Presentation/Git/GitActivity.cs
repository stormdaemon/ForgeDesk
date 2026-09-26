using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Git;

/// <summary>Journals the Git tab's operations (commits, checkouts, merges…) in the activity log.</summary>
internal sealed class GitActivity
{
    private readonly IActivityLog _log;
    private readonly ProjectContext _context;
    private readonly TimeProvider _time;

    public GitActivity(IActivityLog log, ProjectContext context, TimeProvider time)
    {
        _log = log;
        _context = context;
        _time = time;
    }

    public Task SucceededAsync(ActivityKind kind, string title, string? detail = null, string? branch = null) =>
        RecordAsync(kind, ActivityOutcome.Success, title, detail, branch is null ? null : "branch", branch);

    public Task WarningAsync(ActivityKind kind, string title, string? detail = null, string? branch = null) =>
        RecordAsync(kind, ActivityOutcome.Warning, title, detail, branch is null ? null : "branch", branch);

    public Task FailedAsync(ActivityKind kind, string title, ErrorInfo error, string? branch = null) =>
        RecordAsync(kind, ActivityOutcome.Failure, title, error.Hint is null ? error.Message : $"{error.Message} {error.Hint}",
            branch is null ? null : "branch", branch);

    /// <summary>An entry pointing to a commit (commits, amends).</summary>
    public Task CommitAsync(string title, string? detail, string sha) =>
        RecordAsync(ActivityKind.GitCommit, ActivityOutcome.Success, title, detail, "commit", sha);

    private async Task RecordAsync(ActivityKind kind, ActivityOutcome outcome, string title, string? detail, string? refKind, string? refValue)
    {
        try
        {
            await _log.RecordAsync(new ActivityEntry
            {
                ProjectId = _context.ProjectId,
                At = _time.GetLocalNow(),
                Kind = kind,
                Outcome = outcome,
                Title = title,
                Detail = detail,
                RefKind = refKind,
                RefValue = refValue,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Journaling must never turn a successful git operation into a failure.
            System.Diagnostics.Trace.TraceWarning($"Could not record activity '{title}': {ex.Message}");
        }
    }
}
