namespace ForgeDesk.Core.Projects;

/// <summary>Project avatar colors: ten distinct hues that stay readable behind white initials in both themes.</summary>
internal static class AvatarPalette
{
    public static IReadOnlyList<string> Colors { get; } =
    [
        "#E5484D", // red
        "#E2711D", // orange
        "#B8860B", // gold
        "#30A46C", // green
        "#0D9488", // teal
        "#2F80ED", // blue
        "#3E63DD", // indigo
        "#8E4EC6", // purple
        "#D6409F", // pink
        "#A07553", // bronze
    ];

    /// <summary>A random color, preferring hues no other project uses so the dashboard stays varied.</summary>
    public static string Pick(IEnumerable<string?> usedColors, Random random)
    {
        ArgumentNullException.ThrowIfNull(usedColors);
        ArgumentNullException.ThrowIfNull(random);
        var used = usedColors.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = Colors.Where(c => !used.Contains(c)).ToList();
        if (candidates.Count == 0)
        {
            candidates = [.. Colors];
        }

        return candidates[random.Next(candidates.Count)];
    }
}
