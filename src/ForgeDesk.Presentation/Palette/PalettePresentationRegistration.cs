using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Palette;

public static class PalettePresentationRegistration
{
    /// <summary>
    /// Registers the command palette and the shell's own palette sources (projects, navigation,
    /// application and project actions). Features add their sources with
    /// <c>services.AddSingleton&lt;IPaletteSource, MySource&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddPalettePresentation(this IServiceCollection services)
    {
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<IPaletteSource, ProjectsPaletteSource>();
        services.AddSingleton<IPaletteSource, NavigationPaletteSource>();
        services.AddSingleton<IPaletteSource, ShellActionsPaletteSource>();
        return services;
    }
}
