using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Git;

public static class GitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Git domain services. Credential providers (<see cref="IGitCredentialProvider"/>)
    /// are contributed by other domains and picked up automatically.
    /// </summary>
    public static IServiceCollection AddGitServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IGitService, GitService>();
        return services;
    }
}
