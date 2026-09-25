using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Tasks;

public static class TasksPresentationRegistration
{
    /// <summary>Registers the Tasks view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddTasksPresentation(this IServiceCollection services)
    {
        return services;
    }
}
