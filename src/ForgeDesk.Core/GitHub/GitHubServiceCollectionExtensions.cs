using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.GitHub;

public static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the GitHub domain services.</summary>
    public static IServiceCollection AddGitHubServices(this IServiceCollection services)
    {
        return services;
    }
}
