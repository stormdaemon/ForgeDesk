namespace ForgeDesk.Presentation.Workspace;

/// <summary>Tabs of a project workspace, in display order.</summary>
public enum WorkspaceSection
{
    Overview,
    Git,
    Files,
    Terminal,
    Commands,
    Tasks,
    GitHub,
    Releases,
    Insights,
    Activity,
}

/// <summary>
/// A tab inside a project workspace. Section view models are created lazily per project
/// (with the <see cref="ProjectContext"/> injected) and kept alive while the project is open.
/// </summary>
public interface IWorkspaceSectionViewModel
{
    WorkspaceSection Section { get; }

    /// <summary>Called every time the tab becomes visible (first time: load data).</summary>
    Task ActivateAsync();

    /// <summary>Called when the user leaves the tab.</summary>
    void Deactivate();
}

/// <summary>Creates section view models for a project (implemented with DI in the app).</summary>
public interface IWorkspaceSectionFactory
{
    IWorkspaceSectionViewModel Create(WorkspaceSection section, ProjectContext context);

    /// <summary>True when a feature registered a view model for <paramref name="section"/>; other tabs are hidden.</summary>
    bool IsAvailable(WorkspaceSection section) => true;
}

/// <summary>
/// Lets one section ask the workspace to show another (e.g. Overview → "Open Git changes",
/// Insights → a TODO in Files).
/// </summary>
public sealed record WorkspaceNavigationRequest(WorkspaceSection Section, object? Argument = null);

/// <summary>Sections that accept a navigation argument (a file path, a commit sha…).</summary>
public interface INavigationTarget
{
    Task NavigateToAsync(object argument);
}
