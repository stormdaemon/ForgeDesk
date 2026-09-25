using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace ForgeDesk.App.Theming;

/// <summary>
/// Theme state for views that paint their own colors (code and diff views, the terminal).
/// <see cref="Changed"/> is raised on the UI thread after the theme, accent or palette changed;
/// subscribe on Loaded and unsubscribe on Unloaded, since the event is static.
/// </summary>
public static class ThemeProbe
{
    public static event EventHandler? Changed;

    /// <summary>True when the UI currently uses dark surfaces.</summary>
    public static bool IsDark => ApplicationThemeManager.GetAppTheme() switch
    {
        ApplicationTheme.Light => false,
        ApplicationTheme.HighContrast => IsDarkColor(SystemColors.WindowColor),
        _ => true,
    };

    /// <summary>Reads a Color resource (e.g. "ForgeAccentSubtleColor"), with a fallback when missing.</summary>
    public static Color ColorResource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color color ? color : fallback;

    /// <summary>A new frozen brush for a Color resource, for components that freeze what they are given.</summary>
    public static SolidColorBrush FrozenBrush(string colorKey, Color fallback)
    {
        var brush = new SolidColorBrush(ColorResource(colorKey, fallback));
        brush.Freeze();
        return brush;
    }

    internal static bool IsDarkColor(Color color) =>
        ColorMath.RelativeLuminance(new Argb(color.A, color.R, color.G, color.B)) < 0.4;

    internal static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
