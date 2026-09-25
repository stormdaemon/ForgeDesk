using ForgeDesk.App.Controls;
using ForgeDesk.App.Theming;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class ColorMathTests
{
    private static readonly Argb White = Argb.Opaque(255, 255, 255);
    private static readonly Argb Black = Argb.Opaque(0, 0, 0);
    private static readonly Argb DarkSurface = Argb.FromUInt32(0xFF2B2B2B);

    [Fact]
    public void Luminance_spans_black_to_white()
    {
        ColorMath.RelativeLuminance(Black).Should().Be(0);
        ColorMath.RelativeLuminance(White).Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void Black_on_white_has_maximum_contrast() =>
        ColorMath.ContrastRatio(Black, White).Should().BeApproximately(21, 1e-9);

    [Theory]
    [InlineData(0xFFF2762E)]
    [InlineData(0xFF3FB950)]
    [InlineData(0xFF0969DA)]
    [InlineData(0xFF808080)]
    [InlineData(0xFF000000)]
    [InlineData(0xFFFFFFFF)]
    public void Hsl_round_trips(uint argb)
    {
        var color = Argb.FromUInt32(argb);
        var (h, s, l) = ColorMath.ToHsl(color);

        var back = ColorMath.FromHsl(h, s, l);

        Math.Abs(back.R - color.R).Should().BeLessThanOrEqualTo(1);
        Math.Abs(back.G - color.G).Should().BeLessThanOrEqualTo(1);
        Math.Abs(back.B - color.B).Should().BeLessThanOrEqualTo(1);
    }

    // Colors used by AvalonEdit's built-in (light background) highlighting definitions.
    [Theory]
    [InlineData(0xFF0000FF)] // Blue — keywords
    [InlineData(0xFF008000)] // Green — comments
    [InlineData(0xFFA31515)] // string literals
    [InlineData(0xFF800000)] // Maroon
    [InlineData(0xFF000000)] // Black
    [InlineData(0xFF008B8B)] // DarkCyan
    [InlineData(0xFF808080)] // Gray
    [InlineData(0xFF191970)] // MidnightBlue
    [InlineData(0xFFFF0000)] // Red
    public void Syntax_colors_become_readable_on_dark_backgrounds(uint argb)
    {
        var original = Argb.FromUInt32(argb);

        var adapted = ColorMath.AdaptForegroundForDarkBackground(original, DarkSurface);

        ColorMath.ContrastRatio(adapted, DarkSurface).Should().BeGreaterThanOrEqualTo(4.5);
        adapted.A.Should().Be(original.A);
    }

    [Fact]
    public void Dark_adaptation_keeps_the_hue()
    {
        var (hue, _, _) = ColorMath.ToHsl(Argb.FromUInt32(0xFF008000));

        var (adaptedHue, _, _) = ColorMath.ToHsl(ColorMath.AdaptForegroundForDarkBackground(Argb.FromUInt32(0xFF008000), DarkSurface));

        adaptedHue.Should().BeApproximately(hue, 2);
    }

    [Fact]
    public void Background_tints_are_translucent() =>
        ColorMath.AdaptBackgroundForDarkBackground(Argb.FromUInt32(0xFFFFFF00)).A.Should().Be(0x40);

    [Theory]
    [InlineData("#F2762E", 0xFFF2762E)]
    [InlineData("f2762e", 0xFFF2762E)]
    [InlineData("#80F2762E", 0x80F2762E)]
    [InlineData("#fff", 0xFFFFFFFF)]
    public void Hex_colors_parse(string text, uint expected)
    {
        ColorMath.TryParseHex(text, out var color).Should().BeTrue();
        color.Should().Be(Argb.FromUInt32(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Invalid_hex_colors_are_rejected(string? text) =>
        ColorMath.TryParseHex(text, out _).Should().BeFalse();

    [Fact]
    public void Readable_text_picks_the_higher_contrast()
    {
        ColorMath.ReadableTextOn(Argb.FromUInt32(0xFF12877A)).Should().Be(White);
        ColorMath.ReadableTextOn(Argb.FromUInt32(0xFFFFE2B8)).Should().NotBe(White);
    }
}

public class BrandPaletteTests
{
    private static readonly Argb DarkSurface = Argb.FromUInt32(0xFF202020);
    private static readonly Argb LightSurface = Argb.FromUInt32(0xFFF3F3F3);

    public static TheoryData<bool> Themes => new(true, false);

    [Theory]
    [MemberData(nameof(Themes))]
    public void Status_colors_are_readable_on_the_theme_surface(bool dark)
    {
        var palette = BrandPalette.ForEmber(dark);
        var surface = dark ? DarkSurface : LightSurface;

        foreach (var name in new[] { "ForgeSuccess", "ForgeWarning", "ForgeDanger", "ForgeInfo", "ForgeNeutral", "ForgeAccentText" })
        {
            ColorMath.ContrastRatio(palette[name], surface).Should().BeGreaterThanOrEqualTo(3.0, $"{name} must be legible in the {(dark ? "dark" : "light")} theme");
        }
    }

    [Fact]
    public void Dark_palette_uses_the_design_values()
    {
        var palette = BrandPalette.ForEmber(dark: true);

        palette["ForgeAccent"].Should().Be(Argb.FromUInt32(0xFFF2762E));
        palette["ForgeSuccess"].Should().Be(Argb.FromUInt32(0xFF3FB950));
        palette["ForgeWarning"].Should().Be(Argb.FromUInt32(0xFFD29922));
        palette["ForgeDanger"].Should().Be(Argb.FromUInt32(0xFFF85149));
        palette["ForgeInfo"].Should().Be(Argb.FromUInt32(0xFF58A6FF));
        palette["ForgeDiffAddedBackground"].Should().Be(Argb.FromUInt32(0x262EA043));
        palette["ForgeDiffRemovedBackground"].Should().Be(Argb.FromUInt32(0x26F85149));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Both_themes_define_the_same_resources(bool dark) =>
        BrandPalette.ForEmber(dark).Keys.Should().BeEquivalentTo(BrandPalette.ForEmber(!dark).Keys);

    [Fact]
    public void Every_status_brush_used_by_controls_exists()
    {
        var names = BrandPalette.ForEmber(dark: true).Keys.Select(k => k + "Brush").ToHashSet();

        foreach (var kind in Enum.GetValues<StatusKind>())
        {
            names.Should().Contain(StatusResources.BrushKey(kind));
            names.Should().Contain(StatusResources.SubtleBrushKey(kind));
        }
    }

    [Fact]
    public void Custom_accent_flows_to_accent_resources()
    {
        var accent = Argb.FromUInt32(0xFF0078D4);
        var palette = BrandPalette.Build(dark: true, accent, Argb.FromUInt32(0xFF99EBFF));

        palette["ForgeAccent"].Should().Be(accent);
        palette["ForgeRunning"].Should().Be(accent);
        palette["ForgeAccentSubtle"].Should().Be(accent.WithAlpha(0x26));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Ember_accent_shades_get_lighter_in_dark_and_darker_in_light(bool dark)
    {
        var (system, primary, secondary, _) = BrandPalette.EmberAccent(dark);
        var baseLuminance = ColorMath.RelativeLuminance(system);

        if (dark)
        {
            ColorMath.RelativeLuminance(secondary).Should().BeGreaterThan(baseLuminance);
        }
        else
        {
            ColorMath.RelativeLuminance(primary).Should().BeLessThan(baseLuminance);
        }
    }
}
