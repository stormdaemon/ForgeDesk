using System.Globalization;

namespace ForgeDesk.App.Theming;

/// <summary>An sRGB color with alpha, independent of any UI framework.</summary>
internal readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb Opaque(byte r, byte g, byte b) => new(255, r, g, b);

    public static Argb FromUInt32(uint argb) =>
        new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public Argb WithAlpha(byte alpha) => this with { A = alpha };

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>Color computations used for theming: contrast, HSL and dark-mode adaptation.</summary>
internal static class ColorMath
{
    /// <summary>WCAG relative luminance (0 = black, 1 = white), ignoring alpha.</summary>
    public static double RelativeLuminance(Argb color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>WCAG contrast ratio between two opaque colors (1 … 21).</summary>
    public static double ContrastRatio(Argb a, Argb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static (double Hue, double Saturation, double Lightness) ToHsl(Argb color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        if (delta < 1e-9)
        {
            return (0, 0, lightness);
        }

        var saturation = delta / (1 - Math.Abs((2 * lightness) - 1));
        double hue;
        if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, Math.Clamp(saturation, 0, 1), lightness);
    }

    public static Argb FromHsl(double hue, double saturation, double lightness, byte alpha = 255)
    {
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var h = ((hue % 360) + 360) % 360 / 60;
        var x = chroma * (1 - Math.Abs((h % 2) - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = lightness - (chroma / 2);
        return new Argb(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>
    /// Adapts a foreground color designed for light backgrounds (syntax highlighting palettes)
    /// to a dark background: the hue is kept, dark colors are mirrored to light ones, neon
    /// saturation is softened and lightness is raised until the text reaches
    /// <paramref name="minimumContrast"/> against <paramref name="background"/>.
    /// </summary>
    public static Argb AdaptForegroundForDarkBackground(Argb color, Argb background, double minimumContrast = 4.5)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        if (lightness < 0.5)
        {
            lightness = 1 - lightness;
        }

        saturation = Math.Min(saturation, 0.7);
        var adapted = FromHsl(hue, saturation, lightness, color.A);
        while (ContrastRatio(adapted, background) < minimumContrast && lightness < 0.97)
        {
            lightness = Math.Min(0.97, lightness + 0.02);
            adapted = FromHsl(hue, saturation, lightness, color.A);
        }

        return adapted;
    }

    /// <summary>A translucent tint of the color that reads as a highlight on a dark background.</summary>
    public static Argb AdaptBackgroundForDarkBackground(Argb color)
    {
        var (hue, saturation, _) = ToHsl(color);
        return FromHsl(hue, Math.Min(saturation, 0.6), 0.5, 0x40);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    public static Argb ReadableTextOn(Argb background)
    {
        var white = Argb.Opaque(255, 255, 255);
        var black = Argb.Opaque(0x1B, 0x1B, 0x1F);
        return ContrastRatio(white, background) >= ContrastRatio(black, background) ? white : black;
    }

    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB (the leading # is optional).</summary>
    public static bool TryParseHex(string? text, out Argb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = Argb.FromUInt32(value);
        return true;
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel * 255), 0, 255);
}
