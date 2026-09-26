namespace ForgeDesk.Core.Releases;

/// <summary>
/// Finds recently built files that look like release artifacts in the usual output folders
/// (dist/, release/, out/, artifacts/, build/, publish/, **/bin/Release/, target/release/).
/// The search is bounded in depth, entries visited and results.
/// </summary>
internal static class ReleaseAssetFinder
{
    public const int MaxResults = 30;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private const int MaxDepth = 6;
    private const int MaxEntriesVisited = 20_000;

    // Where "bin/Release" folders are looked for: the root and project folders up to src/App/.
    private const int BinSearchDepth = 3;

    // "Releases" is where Velopack (used by .NET desktop apps) writes installers and packages.
    private static readonly string[] OutputFolders = ["dist", "release", "Releases", "out", "artifacts", "build", "publish", Path.Combine("target", "release")];

    private static readonly string[] ArtifactSuffixes =
    [
        ".zip", ".exe", ".msi", ".msix", ".msixbundle", ".nupkg", ".tar.gz", ".tgz", ".dmg", ".AppImage",
        ".deb", ".rpm", ".whl", ".jar", ".apk",
    ];

    // Never artifacts, and potentially huge: not worth walking.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "obj", ".vs", ".idea", ".gradle", "__pycache__", ".venv", "venv", "packages", "deps", "incremental", ".fingerprint",
    };

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,

        // Dot-folders are reported as hidden on Unix; only junctions/symlinks are skipped (they can loop).
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static bool LooksLikeArtifact(string fileName) =>
        ArtifactSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Find(string projectRoot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!Directory.Exists(projectRoot))
        {
            return [];
        }

        var budget = new Budget();
        var roots = OutputFolders
            .Select(folder => Path.Combine(projectRoot, folder))
            .Where(Directory.Exists)
            .Concat(FindBinReleaseFolders(projectRoot, budget, cancellationToken))
            .Distinct(Common.PathUtil.Comparer)
            .ToList();

        var threshold = now.UtcDateTime - MaxAge;
        var found = new Dictionary<string, DateTime>(Common.PathUtil.Comparer);
        foreach (var root in roots)
        {
            Collect(new DirectoryInfo(root), 0, threshold, found, budget, cancellationToken);
        }

        return found
            .OrderByDescending(f => f.Value)
            .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(f => f.Key)
            .ToList();
    }

    private static IEnumerable<string> FindBinReleaseFolders(string projectRoot, Budget budget, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
        pending.Enqueue((new DirectoryInfo(projectRoot), 0));
        while (pending.Count > 0 && budget.TryVisit())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            foreach (var child in SafeDirectories(directory))
            {
                if (child.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    var release = Path.Combine(child.FullName, "Release");
                    if (Directory.Exists(release))
                    {
                        results.Add(release);
                    }
                }
                else if (depth + 1 < BinSearchDepth && !SkippedFolders.Contains(child.Name) && !child.Name.StartsWith('.'))
                {
                    pending.Enqueue((child, depth + 1));
                }
            }
        }

        return results;
    }

    private static void Collect(DirectoryInfo directory, int depth, DateTime thresholdUtc, Dictionary<string, DateTime> found, Budget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!budget.TryVisit())
            {
                return;
            }

            switch (entry)
            {
                case FileInfo file when LooksLikeArtifact(file.Name) && file.LastWriteTimeUtc >= thresholdUtc:
                    found[file.FullName] = file.LastWriteTimeUtc;
                    break;
                case DirectoryInfo child when depth < MaxDepth && !SkippedFolders.Contains(child.Name):
                    Collect(child, depth + 1, thresholdUtc, found, budget, cancellationToken);
                    break;
            }
        }
    }

    private static IEnumerable<DirectoryInfo> SafeDirectories(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories("*", Options).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Budget
    {
        private int _remaining = MaxEntriesVisited;

        public bool TryVisit() => --_remaining >= 0;
    }
}
