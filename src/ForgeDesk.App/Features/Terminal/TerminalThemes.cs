using System.Windows;
using System.Windows.Media;
using ForgeDesk.App.Theming;
using Microsoft.Terminal.Wpf;

namespace ForgeDesk.App.Features.Terminal;

/// <summary>
/// Colors of the integrated terminal, derived from the app theme: the background matches the card
/// surface the terminal sits on, the selection uses the accent, and the 16 ANSI colors are tuned
/// for contrast on dark and light backgrounds (GitHub-like palettes that agree with the Forge
/// status colors).
/// </summary>
internal static class TerminalThemes
{
    // Opaque fallbacks for the window background (Mica makes the real one transparent).
    private static readonly Color DarkBase = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color LightBase = Color.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly Color DarkCard = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF);
    private static readonly Color LightCard = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
    private static readonly Color Ember = Color.FromRgb(0xF2, 0x76, 0x2E);

    private static readonly uint[] DarkPalette =
    [
        Rgb(0x48, 0x4F, 0x58), Rgb(0xFF, 0x7B, 0x72), Rgb(0x3F, 0xB9, 0x50), Rgb(0xD2, 0x99, 0x22),
        Rgb(0x58, 0xA6, 0xFF), Rgb(0xBC, 0x8C, 0xFF), Rgb(0x39, 0xC5, 0xCF), Rgb(0xB1, 0xBA, 0xC4),
        Rgb(0x6E, 0x76, 0x81), Rgb(0xFF, 0xA1, 0x98), Rgb(0x56, 0xD3, 0x64), Rgb(0xE3, 0xB3, 0x41),
        Rgb(0x79, 0xC0, 0xFF), Rgb(0xD2, 0xA8, 0xFF), Rgb(0x56, 0xD4, 0xDD), Rgb(0xF0, 0xF6, 0xFC),
    ];

    private static readonly uint[] LightPalette =
    [
        Rgb(0x24, 0x29, 0x2F), Rgb(0xCF, 0x22, 0x2E), Rgb(0x11, 0x63, 0x29), Rgb(0x9A, 0x67, 0x00),
        Rgb(0x09, 0x69, 0xDA), Rgb(0x82, 0x50, 0xDF), Rgb(0x1B, 0x7C, 0x83), Rgb(0x6E, 0x77, 0x81),
        Rgb(0x57, 0x60, 0x6A), Rgb(0xA4, 0x0E, 0x26), Rgb(0x1A, 0x7F, 0x37), Rgb(0x7D, 0x4E, 0x00),
        Rgb(0x21, 0x8B, 0xFF), Rgb(0xA4, 0x75, 0xF9), Rgb(0x31, 0x92, 0xAA), Rgb(0x8C, 0x95, 0x9F),
    ];

    /// <summary>The first family of the app's mono font ("Cascadia Mono"); the native renderer takes one name.</summary>
    public static string FontFamily
    {
        get
        {
            var source = (Application.Current?.TryFindResource("ForgeMonoFont") as FontFamily)?.Source;
            var first = source?.Split(',')[0].Trim();
            return string.IsNullOrEmpty(first) ? "Cascadia Mono" : first;
        }
    }

    /// <summary>The opaque color of the card surface in the current theme (the terminal background).</summary>
    public static Color Background()
    {
        var dark = ThemeProbe.IsDark;
        var baseColor = (Application.Current?.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)?.Color ?? (dark ? DarkBase : LightBase);
        if (baseColor.A < 0xFF)
        {
            baseColor = dark ? DarkBase : LightBase;
        }

        var card = (Application.Current?.TryFindResource("CardBackgroundFillColorDefaultBrush") as SolidColorBrush)?.Color ?? (dark ? DarkCard : LightCard);
        return Composite(card, baseColor);
    }

    public static TerminalTheme Create()
    {
        var dark = ThemeProbe.IsDark;
        var background = Background();
        var foreground = dark ? Color.FromRgb(0xE6, 0xED, 0xF3) : Color.FromRgb(0x1F, 0x23, 0x28);
        var accent = ThemeProbe.ColorResource("ForgeAccentColor", Ember);
        return new TerminalTheme
        {
            DefaultBackground = ToColorRef(background),
            DefaultForeground = ToColorRef(foreground),
            DefaultSelectionBackground = ToColorRef(Composite(Color.FromArgb(0x80, accent.R, accent.G, accent.B), background)),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = (uint[])(dark ? DarkPalette : LightPalette).Clone(),
        };
    }

    /// <summary>Win32 COLORREF (0x00BBGGRR).</summary>
    public static uint ToColorRef(Color color) => color.R | ((uint)color.G << 8) | ((uint)color.B << 16);

    private static uint Rgb(byte r, byte g, byte b) => ToColorRef(Color.FromRgb(r, g, b));

    /// <summary>Alpha-blends <paramref name="top"/> over an opaque <paramref name="bottom"/>.</summary>
    private static Color Composite(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }
}
