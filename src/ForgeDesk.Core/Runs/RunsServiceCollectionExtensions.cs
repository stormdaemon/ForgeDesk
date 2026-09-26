using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Runs;

public static class RunsServiceCollectionExtensions
{
    /// <summary>Registers the Runs domain services.</summary>
    public static IServiceCollection AddRunsServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IRunService, RunService>();
        return services;
    }
}
