using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Palette;

public static class PalettePresentationRegistration
{
    /// <summary>Registers the Palette view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddPalettePresentation(this IServiceCollection services)
    {
        return services;
    }
}
