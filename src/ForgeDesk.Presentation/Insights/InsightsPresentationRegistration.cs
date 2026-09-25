using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Insights;

public static class InsightsPresentationRegistration
{
    /// <summary>Registers the Insights view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddInsightsPresentation(this IServiceCollection services)
    {
        return services;
    }
}
