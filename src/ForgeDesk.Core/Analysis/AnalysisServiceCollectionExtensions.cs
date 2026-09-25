using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    /// <summary>Registers the Analysis domain services.</summary>
    public static IServiceCollection AddAnalysisServices(this IServiceCollection services)
    {
        return services;
    }
}
