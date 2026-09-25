using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.WorkItems;

public static class WorkItemsServiceCollectionExtensions
{
    /// <summary>Registers the WorkItems domain services.</summary>
    public static IServiceCollection AddWorkItemsServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IWorkItemService, WorkItemService>();
        return services;
    }
}
