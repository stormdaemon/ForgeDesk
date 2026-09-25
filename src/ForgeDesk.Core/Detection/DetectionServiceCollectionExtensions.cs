using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Detection;

public static class DetectionServiceCollectionExtensions
{
    /// <summary>Registers the Detection domain services.</summary>
    public static IServiceCollection AddDetectionServices(this IServiceCollection services)
    {
        return services;
    }
}
