using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.WorkItems;

public static class WorkItemsServiceCollectionExtensions
{
    /// <summary>Registers the WorkItems domain services.</summary>
    public static IServiceCollection AddWorkItemsServices(this IServiceCollection services)
    {
        return services;
    }
}
