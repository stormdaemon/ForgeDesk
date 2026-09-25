using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Activity;

public static class ActivityServiceCollectionExtensions
{
    /// <summary>Registers the Activity domain services.</summary>
    public static IServiceCollection AddActivityServices(this IServiceCollection services)
    {
        return services;
    }
}
