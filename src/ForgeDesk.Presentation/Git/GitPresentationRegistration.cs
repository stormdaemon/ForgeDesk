using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Git;

public static class GitPresentationRegistration
{
    /// <summary>Registers the Git view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddGitPresentation(this IServiceCollection services)
    {
        return services;
    }
}
