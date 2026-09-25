namespace ForgeDesk.Core.Projects;

public sealed record ProjectsChangedEventArgs(string? ProjectId);

/// <summary>Persistent list of the projects the user registered.</summary>
public interface IProjectRegistry
{
    event EventHandler<ProjectsChangedEventArgs>? Changed;

    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Project?> GetAsync(string projectId, CancellationToken cancellationToken = default);

    Task<Project?> FindByPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an existing folder. Throws <c>AlreadyExists</c> if the folder is already
    /// registered and <c>PathNotFound</c> if it does not exist. Detects the GitHub remote.
    /// </summary>
    Task<Project> AddAsync(string path, string? displayName = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the project from ForgeDesk (and its tasks/history). Files are untouched.</summary>
    Task RemoveAsync(string projectId, CancellationToken cancellationToken = default);

    Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Points a project whose folder moved to its new location.</summary>
    Task<Project> RelocateAsync(string projectId, string newPath, CancellationToken cancellationToken = default);

    Task MarkOpenedAsync(string projectId, CancellationToken cancellationToken = default);

    // Cached derived data (JSON blobs) so the UI can render before a refresh completes.
    Task<ProjectSnapshot?> GetCachedSnapshotAsync(string projectId, CancellationToken cancellationToken = default);

    Task SaveSnapshotAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<Detection.ProjectProfile?> GetCachedProfileAsync(string projectId, CancellationToken cancellationToken = default);

    Task SaveProfileAsync(string projectId, Detection.ProjectProfile profile, CancellationToken cancellationToken = default);

    Task<Analysis.ProjectHealthReport?> GetCachedHealthAsync(string projectId, CancellationToken cancellationToken = default);

    Task SaveHealthAsync(string projectId, Analysis.ProjectHealthReport report, CancellationToken cancellationToken = default);
}

/// <summary>Computes <see cref="ProjectSnapshot"/>s (git state, CI, tasks, attention reasons).</summary>
public interface IProjectStatusService
{
    event EventHandler<ProjectSnapshot>? SnapshotUpdated;

    /// <summary>Returns the cached snapshot immediately if any (may be stale).</summary>
    Task<ProjectSnapshot?> GetCachedAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Recomputes the snapshot, persists it and raises <see cref="SnapshotUpdated"/>.</summary>
    Task<ProjectSnapshot> RefreshAsync(Project project, bool includeRemote = true, CancellationToken cancellationToken = default);
}

/// <summary>Clones GitHub (or any git) repositories and registers them.</summary>
public interface IProjectCloneService
{
    Task<Project> CloneAsync(string remoteUrl, string targetDirectory, IProgress<Git.GitProgress>? progress = null, CancellationToken cancellationToken = default);
}
