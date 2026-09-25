using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Runs;

public static class RunsServiceCollectionExtensions
{
    /// <summary>Registers the Runs domain services.</summary>
    public static IServiceCollection AddRunsServices(this IServiceCollection services)
    {
        return services;
    }
}
