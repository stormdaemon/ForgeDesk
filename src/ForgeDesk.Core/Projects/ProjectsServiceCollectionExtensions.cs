using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Projects;

public static class ProjectsServiceCollectionExtensions
{
    /// <summary>Registers the Projects domain services.</summary>
    public static IServiceCollection AddProjectsServices(this IServiceCollection services)
    {
        return services;
    }
}
