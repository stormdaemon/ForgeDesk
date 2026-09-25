using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Settings;

public static class SettingsPresentationRegistration
{
    /// <summary>Registers the Settings view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddSettingsPresentation(this IServiceCollection services)
    {
        return services;
    }
}
