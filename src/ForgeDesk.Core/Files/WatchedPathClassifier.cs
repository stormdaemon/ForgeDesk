namespace ForgeDesk.Core.Files;

internal enum WatchedChangeKind
{
    /// <summary>Noise: git object store, logs, lock files, dependency and build output folders.</summary>
    Ignored,
    WorkingTree,
    GitMetadata,
}

/// <summary>
/// Decides whether a file-system event under a project matters. Inside ".git" only the files that
/// change what git status / branches show count (HEAD, index, refs, packed-refs, FETCH_HEAD,
/// MERGE_HEAD); every git command also rewrites objects and logs, which must not trigger refreshes.
/// </summary>
internal static class WatchedPathClassifier
{
    private static readonly HashSet<string> GitMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "HEAD",
        "index",
        "packed-refs",
        "FETCH_HEAD",
        "MERGE_HEAD",
    };

    /// <summary>
    /// "packages" is a heavy folder for indexing (NuGet's legacy restore folder) but also where
    /// JavaScript monorepos keep their sources: edits there must refresh git status.
    /// </summary>
    private static readonly HashSet<string> WatchedDespiteHeavy = new(StringComparer.OrdinalIgnoreCase) { "packages" };

    /// <param name="relativePath">Path relative to the project root, with either separator.</param>
    /// <param name="isFolderContentChange">
    /// True for a "changed" notification about a folder (as opposed to created/deleted/renamed).
    /// It only echoes activity inside the folder, so for a heavy folder it is noise.
    /// </param>
    public static WatchedChangeKind Classify(string relativePath, bool isFolderContentChange = false)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return WatchedChangeKind.WorkingTree;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return WatchedChangeKind.WorkingTree;
        }

        if (string.Equals(segments[0], ".git", StringComparison.OrdinalIgnoreCase))
        {
            return IsRelevantGitMetadata(segments) ? WatchedChangeKind.GitMetadata : WatchedChangeKind.Ignored;
        }

        // The last segment is the changed entry itself; a heavy folder being created or removed
        // (rm -rf node_modules) changes the tree, what happens inside it does not.
        var inspected = isFolderContentChange ? segments.Length : segments.Length - 1;
        for (var i = 0; i < inspected; i++)
        {
            if (HeavyFolders.IsHeavy(segments[i]) && !WatchedDespiteHeavy.Contains(segments[i]))
            {
                return WatchedChangeKind.Ignored;
            }
        }

        return WatchedChangeKind.WorkingTree;
    }

    private static bool IsRelevantGitMetadata(string[] segments)
    {
        if (segments.Length == 1)
        {
            // The .git folder itself (created by git init, deleted by the user).
            return true;
        }

        if (string.Equals(segments[1], "refs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segments.Length != 2)
        {
            return false;
        }

        // "index.lock" renamed to "index" is reported under the final name.
        return GitMetadataFiles.Contains(segments[1]);
    }
}
