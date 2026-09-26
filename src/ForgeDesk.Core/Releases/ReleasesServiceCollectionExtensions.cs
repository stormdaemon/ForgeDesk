using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Releases;

public static class ReleasesServiceCollectionExtensions
{
    /// <summary>Registers the Releases domain services.</summary>
    public static IServiceCollection AddReleasesServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IReleaseService, ReleaseService>();
        return services;
    }
}
