using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the Files domain services.</summary>
    public static IServiceCollection AddFilesServices(this IServiceCollection services)
    {
        services.TryAddSingleton<GitCli>();
        services.TryAddSingleton<IFileService, FileService>();
        services.TryAddSingleton<IFileIndex, FileIndex>();
        services.TryAddSingleton<IContentSearchService, ContentSearchService>();
        services.TryAddSingleton<IProjectWatcher, ProjectWatcher>();
        return services;
    }
}
