using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Presentation.Settings;

public static class SettingsPresentationRegistration
{
    /// <summary>
    /// Registers the Settings page, its palette entries and the update coordinator. The host
    /// replaces <see cref="IUpdateService"/> with its updater (the app registers Velopack's).
    /// </summary>
    public static IServiceCollection AddSettingsPresentation(this IServiceCollection services)
    {
        services.AddPage<SettingsViewModel>(PageKind.Settings);
        services.TryAddSingleton<IUpdateService, UnavailableUpdateService>();
        services.AddSingleton<UpdateCoordinator>();
        services.AddSingleton<IPaletteSource, SettingsPaletteSource>();
        return services;
    }
}
