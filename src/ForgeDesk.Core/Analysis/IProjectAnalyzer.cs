namespace ForgeDesk.Core.Analysis;

public interface IProjectAnalyzer
{
    /// <summary>Scans the project (bounded) and builds a health report; also caches it in the registry.</summary>
    Task<ProjectHealthReport> AnalyzeAsync(Projects.Project project, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
