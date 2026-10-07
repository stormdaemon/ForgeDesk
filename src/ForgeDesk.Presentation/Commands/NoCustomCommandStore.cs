using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;

namespace ForgeDesk.Presentation.Commands;

/// <summary>Stands in when no custom command store is registered (minimal hosts): no custom commands, and adding one explains why it can't.</summary>
internal sealed class NoCustomCommandStore : ICustomCommandStore
{
    public static readonly NoCustomCommandStore Instance = new();

    public Task<IReadOnlyList<DetectedCommand>> GetAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DetectedCommand>>([]);

    public Task<DetectedCommand> AddAsync(string projectId, string name, string commandLine, CommandCategory category, string? workingDirectory,
        CancellationToken cancellationToken = default) => Task.FromException<DetectedCommand>(Unavailable());

    public Task UpdateAsync(string projectId, DetectedCommand command, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());

    public Task DeleteAsync(string projectId, string commandId, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());

    private static ForgeException Unavailable() =>
        new(ErrorKind.Unknown, "Custom commands can't be saved in this setup.", "Restart ForgeDesk; if it persists, reinstall it.");
}
