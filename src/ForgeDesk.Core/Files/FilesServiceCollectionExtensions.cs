using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the Files domain services.</summary>
    public static IServiceCollection AddFilesServices(this IServiceCollection services)
    {
        return services;
    }
}
