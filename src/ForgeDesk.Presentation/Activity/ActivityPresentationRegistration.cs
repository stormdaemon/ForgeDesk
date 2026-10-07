using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Activity;

public static class ActivityPresentationRegistration
{
    /// <summary>Registers the project Activity tab and the global Activity page.</summary>
    public static IServiceCollection AddActivityPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<ActivitySectionViewModel>(WorkspaceSection.Activity);
        services.AddPage<GlobalActivityViewModel>(PageKind.Activity);
        return services;
    }
}
