namespace ForgeDesk.Core.Common;

public static class PathUtil
{
    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Full path without trailing separator (except for drive roots).</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    public static bool AreSame(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or lies inside it.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var r = Normalize(root);
        var c = Normalize(candidate);
        if (string.Equals(r, c, Comparison))
        {
            return true;
        }

        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Resolves a repository-relative path (forward or back slashes) under a root and
    /// guarantees the result cannot escape the root (no "..\..\" traversal).
    /// </summary>
    public static string ResolveUnder(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedRoot = Normalize(root);
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, rel));
        if (!IsWithin(normalizedRoot, full))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"The path '{relativePath}' is outside of the project folder.");
        }

        return full;
    }

    /// <summary>Relative path using forward slashes (git style).</summary>
    public static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(Normalize(root), fullPath).Replace('\\', '/');

    /// <summary>Converts a git-style relative path to the platform separator.</summary>
    public static string ToPlatform(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
