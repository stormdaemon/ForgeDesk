using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Shell;

public static class ShellPresentationRegistration
{
    /// <summary>Registers the Shell view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddShellPresentation(this IServiceCollection services)
    {
        return services;
    }
}
