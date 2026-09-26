using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Dashboard;

public static class DashboardPresentationRegistration
{
    /// <summary>
    /// Registers the dashboard page and the "Clone repository" flow (which enables every clone entry
    /// point of the shell).
    /// </summary>
    public static IServiceCollection AddDashboardPresentation(this IServiceCollection services)
    {
        services.AddPage<DashboardViewModel>(PageKind.Dashboard);
        services.AddSingleton<CloneRepositoryFlow>();
        services.AddSingleton<ICloneRequestHandler>(sp => sp.GetRequiredService<CloneRepositoryFlow>());
        return services;
    }
}
