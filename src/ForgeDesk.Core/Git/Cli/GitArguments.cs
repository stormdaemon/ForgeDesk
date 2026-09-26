using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

/// <summary>Validation and formatting of values that end up on a git command line.</summary>
internal static class GitArguments
{
    /// <summary>
    /// Budget for the path arguments of one command. CreateProcess allows 32 767 characters, but the
    /// Git for Windows launcher (cmd\git.exe) re-spawns the real git, so we stay well below.
    /// </summary>
    public const int MaxPathArgumentChars = 8_000;

    /// <summary>
    /// Pathspec that matches exactly one repository-relative path: "top" anchors it at the repository
    /// root whatever the working directory, "literal" disables glob characters ("a[1].txt" is a file name).
    /// </summary>
    public static string Pathspec(string relativePath) => ":(top,literal)" + relativePath;

    /// <summary>Normalizes a repository-relative path to git style and rejects anything leaving the repository.</summary>
    public static string RelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ForgeException.InvalidInput("A file path is required.");
        }

        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimEnd('/');
        var segments = normalized.Split('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized)
            || segments.Any(s => s == "..") || normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw ForgeException.InvalidInput($"The path '{path}' is not inside the repository.");
        }

        return normalized;
    }

    public static IReadOnlyList<string> RelativePaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(RelativePath).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Splits arguments into groups whose total length stays under <paramref name="maxChars"/>.</summary>
    public static IEnumerable<IReadOnlyList<string>> Batch(IEnumerable<string> arguments, int maxChars = MaxPathArgumentChars)
    {
        var batch = new List<string>();
        var length = 0;
        foreach (var argument in arguments)
        {
            // +3: separating space and the quotes the runtime may add.
            var cost = argument.Length + 3;
            if (batch.Count > 0 && length + cost > maxChars)
            {
                yield return batch;
                batch = [];
                length = 0;
            }

            batch.Add(argument);
            length += cost;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// A revision, branch, tag or remote name given by the caller. Refuses values git would parse as an
    /// option ("--output=…") or that cannot be a name at all.
    /// </summary>
    public static string Name(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ForgeException.InvalidInput($"A {what} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('-') || trimmed.Any(c => char.IsControl(c)))
        {
            throw ForgeException.InvalidInput($"'{value}' is not a valid {what}.");
        }

        return trimmed;
    }
}
