using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Releases;

public static class ReleasesPresentationRegistration
{
    /// <summary>Registers the Releases view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddReleasesPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<ReleasesViewModel>(WorkspaceSection.Releases);
        services.AddSingleton<IPaletteSource, ReleasesPaletteSource>();
        return services;
    }
}
