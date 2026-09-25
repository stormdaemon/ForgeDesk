using System.Globalization;
using System.Text;
using ForgeDesk.App.Theming;

namespace ForgeDesk.App.Controls;

/// <summary>Initials and a stable color for project avatars.</summary>
internal static class AvatarText
{
    // Mid-tone colors that keep white initials readable and stay distinct side by side.
    private static readonly Argb[] Palette =
    [
        Argb.FromUInt32(0xFFD6455D), // rose
        Argb.FromUInt32(0xFFC2571A), // ember
        Argb.FromUInt32(0xFFA0712A), // bronze
        Argb.FromUInt32(0xFF2F8F5B), // green
        Argb.FromUInt32(0xFF12877A), // teal
        Argb.FromUInt32(0xFF2B7BB9), // blue
        Argb.FromUInt32(0xFF4C5FD5), // indigo
        Argb.FromUInt32(0xFF7C4DC4), // violet
        Argb.FromUInt32(0xFFB2458F), // plum
        Argb.FromUInt32(0xFF5A6B7D), // steel
    ];

    /// <summary>
    /// One or two letters: the first letters of the first two words ("forge-desk", "ForgeDesk" → "FD"),
    /// or the first letter of a single word ("api" → "A"). Blank names give "?".
    /// </summary>
    public static string Initials(string? name)
    {
        var words = SplitWords(name);
        if (words.Count == 0)
        {
            return "?";
        }

        var builder = new StringBuilder(2);
        foreach (var word in words.Take(2))
        {
            builder.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Deterministic palette color for a name (stable across runs and machines).</summary>
    public static Argb ColorFor(string? name)
    {
        // FNV-1a: string.GetHashCode is randomized per process and would reshuffle colors.
        var hash = 2166136261u;
        foreach (var c in (name ?? string.Empty).Trim().ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return Palette[hash % (uint)Palette.Length];
    }

    private static List<string> SplitWords(string? name)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return words;
        }

        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        char previous = '\0';
        foreach (var c in name.Trim())
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
            }
            else
            {
                // camelCase / PascalCase boundary: "forgeDesk" → "forge", "Desk".
                if (char.IsUpper(c) && char.IsLower(previous))
                {
                    Flush();
                }

                current.Append(c);
            }

            previous = c;
        }

        Flush();
        return words;
    }
}
