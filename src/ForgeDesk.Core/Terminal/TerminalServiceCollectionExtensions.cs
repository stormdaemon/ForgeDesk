using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Terminal;

public static class TerminalServiceCollectionExtensions
{
    /// <summary>Registers the Terminal domain services.</summary>
    public static IServiceCollection AddTerminalServices(this IServiceCollection services)
    {
        return services;
    }
}
