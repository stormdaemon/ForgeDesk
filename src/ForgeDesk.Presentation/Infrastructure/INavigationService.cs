using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Top-level pages of the main window.</summary>
public enum PageKind
{
    Dashboard,
    Project,
    Activity,
    Settings,
    Onboarding,
}

public interface INavigationService
{
    /// <summary>The current page view model (DashboardViewModel, ProjectWorkspaceViewModel, …).</summary>
    object? CurrentPage { get; }

    PageKind CurrentKind { get; }

    /// <summary>Id of the project currently shown, if any.</summary>
    string? CurrentProjectId { get; }

    event EventHandler? Navigated;

    void GoToDashboard();

    Task OpenProjectAsync(string projectId, WorkspaceSection? section = null, object? argument = null);

    void OpenSettings(string? section = null);

    void OpenActivity();

    void OpenOnboarding();

    /// <summary>Closes a project workspace (disposing its terminals and watchers).</summary>
    void CloseProject(string projectId);

    bool CanGoBack { get; }

    void GoBack();
}
