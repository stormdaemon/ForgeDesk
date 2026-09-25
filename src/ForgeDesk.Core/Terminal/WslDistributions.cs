using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Terminal;

/// <summary>
/// Parses the output of <c>wsl.exe -l -q</c>. The inbox wsl.exe writes UTF-16LE whatever the
/// console code page (newer builds honour WSL_UTF8=1): read as UTF-8 by the process runner, each
/// character arrives followed by a NUL. Distribution names are ASCII by WSL's own rules, so
/// dropping NULs (and any byte-order mark) restores them exactly.
/// </summary>
internal static partial class WslDistributions
{
    /// <summary>Docker Desktop's internal distributions are not shells anyone wants to open.</summary>
    private static readonly string[] HiddenPrefixes = ["docker-desktop"];

    public static IReadOnlyList<string> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var cleaned = output.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("﻿", string.Empty, StringComparison.Ordinal)
            .Replace("�", string.Empty, StringComparison.Ordinal);

        var names = new List<string>();
        foreach (var raw in cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only valid distribution names: this also drops messages such as
            // "Windows Subsystem for Linux has no installed distributions."
            if (!NameRegex().IsMatch(raw)
                || HiddenPrefixes.Any(p => raw.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || names.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(raw);
        }

        return names;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
