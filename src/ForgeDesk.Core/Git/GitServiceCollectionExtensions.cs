using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Git;

public static class GitServiceCollectionExtensions
{
    /// <summary>Registers the Git domain services.</summary>
    public static IServiceCollection AddGitServices(this IServiceCollection services)
    {
        return services;
    }
}
