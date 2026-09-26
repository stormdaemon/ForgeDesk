using ForgeDesk.Core.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>Creates the workspace (and its <see cref="ProjectContext"/>) of a project being opened.</summary>
public interface IProjectWorkspaceFactory
{
    ProjectWorkspaceViewModel Create(Project project);
}

public sealed class ProjectWorkspaceFactory : IProjectWorkspaceFactory
{
    private readonly IServiceProvider _services;

    public ProjectWorkspaceFactory(IServiceProvider services) => _services = services;

    public ProjectWorkspaceViewModel Create(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var context = ActivatorUtilities.CreateInstance<ProjectContext>(_services, project);
        try
        {
            return ActivatorUtilities.CreateInstance<ProjectWorkspaceViewModel>(_services, context);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }
}
