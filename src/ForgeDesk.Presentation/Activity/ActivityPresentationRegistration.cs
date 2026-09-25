using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Activity;

public static class ActivityPresentationRegistration
{
    /// <summary>Registers the Activity view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddActivityPresentation(this IServiceCollection services)
    {
        return services;
    }
}
