namespace ForgeDesk.Core.Detection;

/// <summary>Analyzes a folder and produces a <see cref="ProjectProfile"/>. Never throws for odd projects.</summary>
public interface IProjectDetector
{
    Task<ProjectProfile> DetectAsync(string projectRoot, CancellationToken cancellationToken = default);
}

/// <summary>
/// One ecosystem-specific detector (Node, .NET, Rust…). The composite detector runs all of
/// them against a shared <see cref="DetectionContext"/> and merges their contributions.
/// </summary>
public interface IEcosystemDetector
{
    string Name { get; }

    Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken);
}

/// <summary>User-defined commands stored per project.</summary>
public interface ICustomCommandStore
{
    Task<IReadOnlyList<DetectedCommand>> GetAsync(string projectId, CancellationToken cancellationToken = default);

    Task<DetectedCommand> AddAsync(string projectId, string name, string commandLine, CommandCategory category, string? workingDirectory, CancellationToken cancellationToken = default);

    Task UpdateAsync(string projectId, DetectedCommand command, CancellationToken cancellationToken = default);

    Task DeleteAsync(string projectId, string commandId, CancellationToken cancellationToken = default);
}
