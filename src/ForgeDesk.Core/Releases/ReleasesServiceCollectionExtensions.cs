using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Releases;

public static class ReleasesServiceCollectionExtensions
{
    /// <summary>Registers the Releases domain services.</summary>
    public static IServiceCollection AddReleasesServices(this IServiceCollection services)
    {
        return services;
    }
}
