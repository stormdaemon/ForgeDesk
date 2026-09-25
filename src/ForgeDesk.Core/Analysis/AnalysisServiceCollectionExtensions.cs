using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    /// <summary>Registers the Analysis domain services.</summary>
    public static IServiceCollection AddAnalysisServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectAnalyzer, ProjectAnalyzer>();
        return services;
    }
}
