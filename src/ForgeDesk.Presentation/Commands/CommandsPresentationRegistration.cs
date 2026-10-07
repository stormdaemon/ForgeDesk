using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Commands;

public static class CommandsPresentationRegistration
{
    /// <summary>Registers the Commands view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddCommandsPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<CommandsSectionViewModel>(WorkspaceSection.Commands);
        services.AddSingleton<IPaletteSource, CommandsPaletteSource>();

        // Resolved once at startup by the app, so completions are notified for every project.
        services.AddSingleton<RunNotificationsCoordinator>();
        return services;
    }
}
