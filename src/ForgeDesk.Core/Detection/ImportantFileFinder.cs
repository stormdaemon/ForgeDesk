namespace ForgeDesk.Core.Detection;

/// <summary>Finds the well-known project files (README, LICENSE, CI workflows…) that exist.</summary>
internal static class ImportantFileFinder
{
    // GitHub reads community files from the root, .github/ and docs/.
    private static readonly string[] CommunityFolders = [string.Empty, ".github", "docs"];

    public static IReadOnlyList<string> Find(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = new List<string>();

        void AddFirst(IEnumerable<string> candidates)
        {
            var first = candidates.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (first is not null)
            {
                result.Add(first);
            }
        }

        AddFirst(context.FilesIn(string.Empty).Where(f => StartsWith(f, "README")));
        AddFirst(context.FilesIn(string.Empty).Where(f => StartsWith(f, "LICENSE") || StartsWith(f, "LICENCE") || StartsWith(f, "COPYING")));
        AddFirst(context.FilesIn(string.Empty).Where(f => StartsWith(f, "CHANGELOG") || StartsWith(f, "CHANGES")));
        AddFirst(CommunityFolders.SelectMany(context.FilesIn).Where(f => StartsWith(RelativePaths.FileName(f), "CONTRIBUTING")));
        AddFirst(CommunityFolders.SelectMany(context.FilesIn).Where(f => RelativePaths.FileName(f).Equals("SECURITY.md", StringComparison.OrdinalIgnoreCase)));

        foreach (var exact in new[] { ".gitignore", ".editorconfig", "Dockerfile" })
        {
            if (context.FirstExisting(exact) is { } found)
            {
                result.Add(found);
            }
        }

        if (context.Files.Any(IsWorkflowFile))
        {
            result.Add(".github/workflows");
        }

        return result;
    }

    public static bool IsWorkflowFile(string relativePath) =>
        RelativePaths.DirectoryOf(relativePath).Equals(".github/workflows", StringComparison.OrdinalIgnoreCase)
        && (relativePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || relativePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    private static bool StartsWith(string fileName, string prefix) => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
