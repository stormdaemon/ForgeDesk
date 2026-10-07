using ForgeDesk.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.App.Services.Updates;

public static class UpdateServicesRegistration
{
    /// <summary>Replaces the Presentation's placeholder updater with Velopack's.</summary>
    public static IServiceCollection AddForgeDeskUpdates(this IServiceCollection services)
    {
        services.AddSingleton<IUpdateService, VelopackUpdateService>();
        return services;
    }
}
