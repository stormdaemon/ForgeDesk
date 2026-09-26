using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Git;

public static class GitPresentationRegistration
{
    /// <summary>Registers the Git view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddGitPresentation(this IServiceCollection services)
    {
        services.AddSingleton<GitViewPreferences>();
        services.AddWorkspaceSection<GitSectionViewModel>(WorkspaceSection.Git);
        services.AddSingleton<IPaletteSource, GitPaletteSource>();
        return services;
    }
}
