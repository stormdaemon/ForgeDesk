using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// Project-level actions shared by the sidebar, the palette, the workspace and window activation.
/// Every method reports failures through notifications (never throws for expected errors) and
/// asks for confirmation where the action is destructive.
/// </summary>
public interface IProjectActions
{
    /// <summary>True when a clone flow is available (see <see cref="ICloneRequestHandler"/>).</summary>
    bool CanClone { get; }

    /// <summary>Asks for a folder, registers it (or finds it when already registered) and opens it.</summary>
    Task AddLocalProjectAsync();

    Task CloneRepositoryAsync();

    /// <summary>Opens the project registered at <paramref name="folderPath"/>, registering the folder first when needed.</summary>
    Task<bool> AddOrOpenFolderAsync(string folderPath);

    /// <summary>Opens a registered project; returns false (after notifying) when it cannot be opened.</summary>
    Task<bool> OpenAsync(string projectId, WorkspaceSection? section = null, object? argument = null);

    Task TogglePinAsync(string projectId);

    Task RenameAsync(string projectId);

    /// <summary>Asks for confirmation, then removes the project from ForgeDesk (files stay on disk).</summary>
    Task<bool> RemoveAsync(string projectId);

    void OpenInExplorer(string path);
}
