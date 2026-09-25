using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Terminal;

public static class TerminalServiceCollectionExtensions
{
    /// <summary>Registers the Terminal domain services.</summary>
    public static IServiceCollection AddTerminalServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IShellEnvironment, SystemShellEnvironment>();
        services.TryAddSingleton<IShellDiscovery, ShellDiscovery>();
        return services;
    }
}
