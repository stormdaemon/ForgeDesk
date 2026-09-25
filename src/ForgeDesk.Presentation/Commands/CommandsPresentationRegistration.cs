using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Commands;

public static class CommandsPresentationRegistration
{
    /// <summary>Registers the Commands view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddCommandsPresentation(this IServiceCollection services)
    {
        return services;
    }
}
