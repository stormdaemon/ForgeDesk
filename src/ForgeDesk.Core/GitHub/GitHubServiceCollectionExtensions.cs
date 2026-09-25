using ForgeDesk.Core.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.GitHub;

public static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the GitHub domain services.</summary>
    public static IServiceCollection AddGitHubServices(this IServiceCollection services)
    {
        services.TryAddSingleton<GitHubSession>();
        services.TryAddSingleton<GitHubClientFactory>();
        services.TryAddSingleton<GitHubClientProvider>();
        services.TryAddSingleton<GitHubResponseCache>();
        services.TryAddSingleton<ReleaseAssetUploader>();
        services.TryAddSingleton<GitHubTokenValidator>();
        services.TryAddSingleton<GitCredentialManagerClient>();
        services.TryAddSingleton<IGitHubCliLocator, GitHubCliLocator>();
        services.TryAddSingleton<GitHubCliClient>();
        services.TryAddSingleton<IGitHubAccountService, GitHubAccountService>();
        services.TryAddSingleton<IGitHubService, GitHubService>();

        // Enumerable registration: other domains may contribute credential providers too.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitCredentialProvider, GitHubCredentialProvider>());
        return services;
    }
}
