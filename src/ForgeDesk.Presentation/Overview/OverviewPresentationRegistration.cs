using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Overview;

public static class OverviewPresentationRegistration
{
    /// <summary>Registers the Overview view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddOverviewPresentation(this IServiceCollection services)
    {
        return services;
    }
}
