using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Projects;

public static class ProjectsServiceCollectionExtensions
{
    /// <summary>Registers the Projects domain services.</summary>
    public static IServiceCollection AddProjectsServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectRegistry, ProjectRegistry>();
        services.TryAddSingleton<IProjectStatusService, ProjectStatusService>();
        services.TryAddSingleton<IProjectCloneService, ProjectCloneService>();
        return services;
    }
}
