using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Files;

public static class FilesPresentationRegistration
{
    /// <summary>Registers the Files view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddFilesPresentation(this IServiceCollection services)
    {
        return services;
    }
}
