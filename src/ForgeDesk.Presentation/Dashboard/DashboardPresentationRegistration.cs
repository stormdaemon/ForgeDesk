using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Dashboard;

public static class DashboardPresentationRegistration
{
    /// <summary>Registers the Dashboard view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddDashboardPresentation(this IServiceCollection services)
    {
        return services;
    }
}
