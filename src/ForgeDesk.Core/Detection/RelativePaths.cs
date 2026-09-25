namespace ForgeDesk.Core.Detection;

/// <summary>Helpers for the forward-slash relative paths used by <see cref="DetectionContext"/>.</summary>
internal static class RelativePaths
{
    /// <summary>Number of folders above the file: 0 for "package.json", 2 for "apps/web/package.json".</summary>
    public static int Depth(string relativePath) => relativePath.Count(c => c == '/');

    public static bool IsAtRoot(string relativePath) => !relativePath.Contains('/', StringComparison.Ordinal);

    /// <summary>Parent folder ("" for root files).</summary>
    public static string DirectoryOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    public static string FileName(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    /// <summary>Joins a folder and a name ("" folder = root).</summary>
    public static string Combine(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory}/{name}";

    /// <summary>First folder segment, or null for root files.</summary>
    public static string? TopFolder(string relativePath)
    {
        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? null : relativePath[..slash];
    }

    public static bool IsUnder(string relativePath, string directory) =>
        directory.Length == 0 || relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files of the context located directly in <paramref name="directory"/>.</summary>
    public static IEnumerable<string> FilesIn(this DetectionContext context, string directory) =>
        context.Files.Where(f => string.Equals(DirectoryOf(f), directory, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when at least one scanned file lies under <paramref name="directory"/>.</summary>
    public static bool HasDirectory(this DetectionContext context, string directory) =>
        context.Files.Any(f => IsUnder(f, directory));

    /// <summary>The first existing root file among <paramref name="candidates"/> (case-insensitive), with its real spelling.</summary>
    public static string? FirstExisting(this DetectionContext context, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (context.Exists(candidate))
            {
                return context.Files.FirstOrDefault(f => string.Equals(f, candidate, StringComparison.OrdinalIgnoreCase)) ?? candidate;
            }
        }

        return null;
    }
}
