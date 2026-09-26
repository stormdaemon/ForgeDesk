using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>Journals git operations run from the workspace header in the activity log.</summary>
internal sealed class WorkspaceActivity
{
    private readonly IActivityLog _log;
    private readonly ProjectContext _context;
    private readonly TimeProvider _time;

    public WorkspaceActivity(IActivityLog log, ProjectContext context, TimeProvider time)
    {
        _log = log;
        _context = context;
        _time = time;
    }

    public Task SucceededAsync(ActivityKind kind, string title, string? detail = null, string? branch = null) =>
        RecordAsync(kind, ActivityOutcome.Success, title, detail, branch);

    public Task FailedAsync(ActivityKind kind, string title, ErrorInfo error, string? branch = null) =>
        RecordAsync(kind, ActivityOutcome.Failure, title, error.Hint is null ? error.Message : $"{error.Message} {error.Hint}", branch);

    private async Task RecordAsync(ActivityKind kind, ActivityOutcome outcome, string title, string? detail, string? branch)
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
                RefKind = branch is null ? null : "branch",
                RefValue = branch,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Journaling must never turn a successful git operation into a failure.
            System.Diagnostics.Trace.TraceWarning($"Could not record activity '{title}': {ex.Message}");
        }
    }
}
