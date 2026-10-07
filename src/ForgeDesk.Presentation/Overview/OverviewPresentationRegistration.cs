using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Overview;

public static class OverviewPresentationRegistration
{
    /// <summary>Registers the Overview tab (the default tab of a project).</summary>
    public static IServiceCollection AddOverviewPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<OverviewViewModel>(WorkspaceSection.Overview);
        return services;
    }
}
