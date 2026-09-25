namespace ForgeDesk.App.Theming;

/// <summary>
/// The Forge brand colors for each theme. Status colors are the DESIGN.md values on dark
/// surfaces and deeper shades on light surfaces, so that text and icons keep a readable
/// contrast in both. Each entry becomes a <c>{Name}Color</c> and a <c>{Name}Brush</c> resource.
/// </summary>
internal static class BrandPalette
{
    public static readonly Argb Ember = Argb.FromUInt32(0xFFF2762E);
    public static readonly Argb EmberLight = Argb.FromUInt32(0xFFFF9A57);
    public static readonly Argb EmberDark = Argb.FromUInt32(0xFFC9551A);

    /// <summary>Ember accent shades handed to WPF-UI (system, primary, secondary, tertiary).</summary>
    public static (Argb System, Argb Primary, Argb Secondary, Argb Tertiary) EmberAccent(bool dark) => dark
        ? (Ember, Argb.FromUInt32(0xFFF98A4F), EmberLight, Argb.FromUInt32(0xFFFFB988))
        : (Ember, EmberDark, Argb.FromUInt32(0xFFA94613), Argb.FromUInt32(0xFF86370E));

    /// <summary>Resource names (without the Color/Brush suffix) to colors for one theme.</summary>
    /// <param name="dark">Dark surfaces (true) or light surfaces (false).</param>
    /// <param name="accent">Accent fill (ember unless the user picked the Windows accent).</param>
    /// <param name="accentText">Accent for text and icons, readable on the theme's surfaces.</param>
    public static IReadOnlyDictionary<string, Argb> Build(bool dark, Argb accent, Argb accentText)
    {
        var success = Argb.FromUInt32(dark ? 0xFF3FB950 : 0xFF1A7F37);
        var warning = Argb.FromUInt32(dark ? 0xFFD29922 : 0xFF9A6700);
        var danger = Argb.FromUInt32(dark ? 0xFFF85149 : 0xFFCF222E);
        var info = Argb.FromUInt32(dark ? 0xFF58A6FF : 0xFF0969DA);
        var neutral = Argb.FromUInt32(dark ? 0xFF8B949E : 0xFF6E7781);
        const byte subtle = 0x26;

        return new Dictionary<string, Argb>(StringComparer.Ordinal)
        {
            ["ForgeAccent"] = accent,
            ["ForgeAccentText"] = accentText,
            ["ForgeAccentSubtle"] = accent.WithAlpha(subtle),
            ["ForgeRunning"] = accent,
            ["ForgeSuccess"] = success,
            ["ForgeSuccessSubtle"] = success.WithAlpha(subtle),
            ["ForgeWarning"] = warning,
            ["ForgeWarningSubtle"] = warning.WithAlpha(subtle),
            ["ForgeDanger"] = danger,
            ["ForgeDangerSubtle"] = danger.WithAlpha(subtle),
            ["ForgeInfo"] = info,
            ["ForgeInfoSubtle"] = info.WithAlpha(subtle),
            ["ForgeNeutral"] = neutral,
            ["ForgeNeutralSubtle"] = neutral.WithAlpha(subtle),
            ["ForgeDiffAddedBackground"] = Argb.FromUInt32(0x262EA043),
            ["ForgeDiffRemovedBackground"] = Argb.FromUInt32(0x26F85149),
            ["ForgeDiffAddedGutter"] = success,
            ["ForgeDiffRemovedGutter"] = danger,
            ["ForgeDiffHunkBackground"] = info.WithAlpha(dark ? (byte)0x1A : (byte)0x14),
            ["ForgeSkeleton"] = dark ? Argb.FromUInt32(0x14FFFFFF) : Argb.FromUInt32(0x0F000000),
            ["ForgeSkeletonShimmer"] = dark ? Argb.FromUInt32(0x1AFFFFFF) : Argb.FromUInt32(0x99FFFFFF),
        };
    }

    /// <summary>The ember palette for a theme.</summary>
    public static IReadOnlyDictionary<string, Argb> ForEmber(bool dark) =>
        Build(dark, Ember, dark ? EmberLight : Argb.FromUInt32(0xFFB8501A));
}
