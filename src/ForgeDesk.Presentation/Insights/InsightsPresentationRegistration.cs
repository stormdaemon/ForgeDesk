using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Insights;

public static class InsightsPresentationRegistration
{
    /// <summary>Registers the Insights tab (project health report).</summary>
    public static IServiceCollection AddInsightsPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<InsightsViewModel>(WorkspaceSection.Insights);
        return services;
    }
}
