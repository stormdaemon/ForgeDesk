using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.GitHub;

public static class GitHubPresentationRegistration
{
    /// <summary>Registers the GitHub view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddGitHubPresentation(this IServiceCollection services)
    {
        return services;
    }
}
