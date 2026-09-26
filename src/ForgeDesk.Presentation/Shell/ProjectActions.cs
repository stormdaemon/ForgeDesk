using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Shell;

public sealed class ProjectActions : IProjectActions
{
    private const int MaxNameLength = 100;

    private readonly IProjectRegistry _registry;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly ISettingsService _settings;
    private readonly ICloneRequestHandler? _cloneHandler;
    private readonly ILogger<ProjectActions> _logger;

    public ProjectActions(
        IProjectRegistry registry,
        INavigationService navigation,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        ISettingsService settings,
        IEnumerable<ICloneRequestHandler> cloneHandlers,
        ILogger<ProjectActions> logger)
    {
        ArgumentNullException.ThrowIfNull(cloneHandlers);
        _registry = registry;
        _navigation = navigation;
        _dialogs = dialogs;
        _notifications = notifications;
        _shell = shell;
        _settings = settings;
        _cloneHandler = cloneHandlers.LastOrDefault();
        _logger = logger;
    }

    public bool CanClone => _cloneHandler is not null;

    public async Task AddLocalProjectAsync()
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync("Add a local project", _settings.Current.DefaultCloneDirectory).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not open the folder picker");
            return;
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            await AddOrOpenFolderAsync(folder).ConfigureAwait(true);
        }
    }

    public async Task CloneRepositoryAsync()
    {
        if (_cloneHandler is null)
        {
            return;
        }

        try
        {
            await _cloneHandler.RequestCloneAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not clone the repository");
        }
    }

    public async Task<bool> AddOrOpenFolderAsync(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        Project project;
        try
        {
            project = await _registry.FindByPathAsync(folderPath).ConfigureAwait(true)
                ?? await AddAsync(folderPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not add the project");
            return false;
        }

        return await OpenAsync(project.Id).ConfigureAwait(true);
    }

    public async Task<bool> OpenAsync(string projectId, WorkspaceSection? section = null, object? argument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        try
        {
            await _navigation.OpenProjectAsync(projectId, section, argument).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open project {ProjectId}", projectId);
            Report(ex, "Could not open the project");
            return false;
        }
    }

    public async Task TogglePinAsync(string projectId)
    {
        try
        {
            var project = await RequireAsync(projectId).ConfigureAwait(true);
            await _registry.UpdateAsync(project with { IsPinned = !project.IsPinned }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not update the project");
        }
    }

    public async Task RenameAsync(string projectId)
    {
        try
        {
            var project = await RequireAsync(projectId).ConfigureAwait(true);
            var name = await _dialogs.PromptAsync(new PromptOptions
            {
                Title = "Rename project",
                Message = "The new name is only used in ForgeDesk; the folder keeps its name.",
                InitialValue = project.Name,
                Placeholder = "Project name",
                ConfirmText = "Rename",
                Validate = ValidateName,
            }).ConfigureAwait(true);

            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, project.Name, StringComparison.Ordinal))
            {
                return;
            }

            await _registry.UpdateAsync(project with { Name = trimmed }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not rename the project");
        }
    }

    public async Task<bool> RemoveAsync(string projectId)
    {
        try
        {
            var project = await RequireAsync(projectId).ConfigureAwait(true);
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = $"Remove {project.Name} from ForgeDesk?",
                Message = $"The folder and all its files stay on disk:\n{project.Path}\n\n"
                    + "ForgeDesk forgets the project's tasks, run history and activity.",
                ConfirmText = "Remove from ForgeDesk",
                IsDestructive = true,
            }).ConfigureAwait(true);
            if (!confirmed)
            {
                return false;
            }

            await _registry.RemoveAsync(project.Id).ConfigureAwait(true);
            _navigation.CloseProject(project.Id);
            _notifications.Show($"Removed {project.Name} from ForgeDesk", "Its files were kept on disk.", NotificationSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            Report(ex, "Could not remove the project");
            return false;
        }
    }

    public void OpenInExplorer(string path)
    {
        try
        {
            _shell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            Report(ex, "Could not open the folder");
        }
    }

    internal static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Enter a name.";
        }

        return trimmed.Length > MaxNameLength ? $"Use at most {MaxNameLength} characters." : null;
    }

    private async Task<Project> AddAsync(string folderPath)
    {
        try
        {
            var project = await _registry.AddAsync(folderPath).ConfigureAwait(true);
            _notifications.Show($"Added {project.Name}", project.Path, NotificationSeverity.Success);
            return project;
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.AlreadyExists)
        {
            // Registered under an equivalent spelling of the path (case, trailing separator…).
            var existing = await _registry.FindByPathAsync(folderPath).ConfigureAwait(true);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    /// <summary>Failures become error notifications; cancellations stay silent.</summary>
    private void Report(Exception exception, string title)
    {
        if (!exception.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(exception, title));
        }
    }

    private async Task<Project> RequireAsync(string projectId) =>
        await _registry.GetAsync(projectId).ConfigureAwait(true)
        ?? throw new ForgeException(ErrorKind.NotFound, "This project is no longer registered in ForgeDesk.",
            "It may have been removed from another window. Add its folder again to use it.");
}
