namespace ForgeDesk.Core.Analysis;

/// <summary>Dependency, build-output and tooling folders that are never analyzed.</summary>
internal static class HeavyFolders
{
    /// <summary>Skipped when walking a folder that has no Git information.</summary>
    private static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "target", "dist", "build", ".venv", "venv", "__pycache__",
        ".next", ".nuxt", "packages", ".gradle", ".idea", ".vs",
    };

    /// <summary>
    /// Skipped even when Git lists their files. In a Git listing .gitignore has already removed
    /// build output, and "bin", "build" and "packages" are commonly real source there (Ruby and
    /// Node bin/ scripts, build/ scripts, JavaScript monorepo packages/), so they are kept.
    /// </summary>
    private static readonly HashSet<string> NeverSource = new(All.Except(["bin", "build", "packages"], StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static bool IsHeavy(string folderName) => All.Contains(folderName);

    /// <summary>True when a Git-listed path lies in a folder that never holds hand-written source.</summary>
    public static bool IsInNeverSourceFolder(string relativePath)
    {
        var start = 0;
        while (true)
        {
            var slash = relativePath.IndexOf('/', start);
            if (slash < 0)
            {
                return false;
            }

            if (NeverSource.Contains(relativePath[start..slash]))
            {
                return true;
            }

            start = slash + 1;
        }
    }
}
