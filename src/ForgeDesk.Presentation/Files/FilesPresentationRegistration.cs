using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Files;

public static class FilesPresentationRegistration
{
    /// <summary>Registers the Files view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddFilesPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<FilesSectionViewModel>(WorkspaceSection.Files);
        services.AddSingleton<IPaletteSource, FilesPaletteSource>();
        return services;
    }
}
