using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Releases;

public interface IReleaseService
{
    Task<ReleaseContext> PrepareAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the release steps in order, reporting each transition. Never throws for step
    /// failures: the result carries the failed step and error so the UI can show a timeline.
    /// </summary>
    Task<ReleaseResult> ExecuteAsync(Project project, ReleasePlan plan, IProgress<ReleaseStepUpdate>? progress = null, CancellationToken cancellationToken = default);
}
