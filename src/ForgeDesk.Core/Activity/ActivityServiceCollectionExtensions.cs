using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Activity;

public static class ActivityServiceCollectionExtensions
{
    /// <summary>Registers the Activity domain services.</summary>
    public static IServiceCollection AddActivityServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IActivityLog, ActivityLog>();
        return services;
    }
}
