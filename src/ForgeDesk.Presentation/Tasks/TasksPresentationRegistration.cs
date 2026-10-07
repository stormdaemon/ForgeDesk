using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Tasks;

public static class TasksPresentationRegistration
{
    /// <summary>Registers the Tasks view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddTasksPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<TasksSectionViewModel>(WorkspaceSection.Tasks);
        services.AddSingleton<IPaletteSource, TasksPaletteSource>();
        return services;
    }
}
