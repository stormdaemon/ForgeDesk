using System.Collections.Frozen;

namespace ForgeDesk.Core.Detection;

/// <summary>Folder names that hold dependencies, caches or build output and must not be scanned.</summary>
internal static class HeavyFolders
{
    // Dependency stores, caches and tool state: never source code, even when someone committed them.
    private static readonly FrozenSet<string> DependencyAndCacheFolders = new[]
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", ".venv", "venv", "__pycache__",
        ".next", ".nuxt", ".svelte-kit", ".angular", ".turbo", ".parcel-cache", ".gradle", ".idea", ".vs",
        ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".dart_tool", ".terraform", ".expo", ".docusaurus",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Usually build output. The managed walker skips them, but files git tracks inside them are kept:
    // git knows what is source (bin/ scripts of a Rails app, build/ scripts, JS workspaces in packages/).
    private static readonly FrozenSet<string> OutputFolders = new[]
    {
        "bin", "obj", "target", "dist", "build", "out", "packages",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for dependency/cache folders that are never indexed, whatever their origin.</summary>
    public static bool IsDependencyOrCache(string folderName) => DependencyAndCacheFolders.Contains(folderName);

    /// <summary>True for build-output folders (bin, obj, target, dist…).</summary>
    public static bool IsBuildOutput(string folderName) => OutputFolders.Contains(folderName);

    public static bool IsHeavy(string folderName) => IsDependencyOrCache(folderName) || IsBuildOutput(folderName);

    /// <summary>True when any folder segment of a relative path (forward slashes) is a dependency/cache folder.</summary>
    public static bool IsUnderDependencyOrCache(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IsDependencyOrCache(segments[i]))
            {
                return true;
            }
        }

        return false;
    }
}
