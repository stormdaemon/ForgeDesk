using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Detection;

/// <summary>A scanned file: relative path (forward slashes) and size in bytes.</summary>
internal sealed record ScannedFile(string RelativePath, long Length);

internal sealed record FileScanResult(IReadOnlyList<ScannedFile> Files, bool Truncated);

/// <summary>
/// Produces the bounded file list detection works on: <c>git ls-files</c> when the folder is the
/// root of a repository (it honors .gitignore), otherwise a managed breadth-first walk that skips
/// heavy folders, symbolic links, junctions, hidden system folders and unreadable folders.
/// Breadth-first order keeps root manifests in the list even when the scan is truncated.
/// </summary>
internal sealed class ProjectFileScanner
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    private static readonly string[] JavaScriptWorkspaceMarkers = ["package.json", "pnpm-workspace.yaml", "lerna.json"];
    private static readonly string[] DotNetProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    private static readonly string[] BuildOutputMarkers =
    [
        "CMakeLists.txt", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
        "package.json", "setup.py", "pyproject.toml", "meson.build",
    ];

    private readonly IGitService _git;
    private readonly ILogger _logger;

    public ProjectFileScanner(IGitService git, ILogger logger)
    {
        _git = git;
        _logger = logger;
    }

    public async Task<FileScanResult> ScanAsync(string root, int maxFiles, CancellationToken cancellationToken)
    {
        if (IsRepositoryRoot(root))
        {
            try
            {
                var listed = await _git.ListFilesAsync(root, cancellationToken).ConfigureAwait(false);
                return await Task.Run(() => FromGitListing(root, listed, maxFiles, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (ForgeException ex)
            {
                // git missing, repository damaged, "dubious ownership"…: the walker still gives a good picture.
                _logger.LogDebug(ex, "git ls-files failed for {Root}; walking the folder instead", root);
            }
        }

        return await Task.Run(() => Walk(root, maxFiles, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRepositoryRoot(string root)
    {
        var dotGit = Path.Combine(root, ".git");

        // Worktrees and submodules have a ".git" file pointing to the real git directory.
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static FileScanResult FromGitListing(string root, IReadOnlyList<string> listed, int maxFiles, CancellationToken cancellationToken)
    {
        var candidates = listed
            .Select(p => p.Replace('\\', '/'))
            .Where(p => p.Length > 0 && !p.StartsWith('/') && !p.Split('/').Contains(".."))
            .Where(p => !HeavyFolders.IsUnderDependencyOrCache(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(RelativePaths.Depth)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

        var files = new List<ScannedFile>(Math.Min(candidates.Count, maxFiles));
        var sizes = new DirectorySizeCache(root);
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Tracked files deleted from the working tree and submodule folders have no size: skip them.
            if (sizes.TryGetLength(path) is not { } length)
            {
                continue;
            }

            if (files.Count == maxFiles)
            {
                return new FileScanResult(files, true);
            }

            files.Add(new ScannedFile(path, length));
        }

        return new FileScanResult(files, false);
    }

    private static FileScanResult Walk(string root, int maxFiles, CancellationToken cancellationToken)
    {
        var files = new List<ScannedFile>();
        var pending = new Queue<(string FullPath, string Relative)>();
        pending.Enqueue((root, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relative) = pending.Dequeue();

            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ListingOptions)
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            var siblingFiles = entries.OfType<FileInfo>().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    continue;
                }

                var entryRelative = RelativePaths.Combine(relative, entry.Name);
                if (entry is FileInfo file)
                {
                    if (files.Count == maxFiles)
                    {
                        return new FileScanResult(files, true);
                    }

                    files.Add(new ScannedFile(entryRelative, SafeLength(file)));
                }
                else if (ShouldDescend((DirectoryInfo)entry, siblingFiles))
                {
                    pending.Enqueue((entry.FullName, entryRelative));
                }
            }
        }

        return new FileScanResult(files, false);
    }

    private static bool ShouldDescend(DirectoryInfo directory, HashSet<string> siblingFiles)
    {
        var attributes = directory.Attributes;
        if ((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.System) != 0)
        {
            return false;
        }

        var name = directory.Name;
        if (HeavyFolders.IsDependencyOrCache(name))
        {
            return false;
        }

        if (!HeavyFolders.IsBuildOutput(name))
        {
            return true;
        }

        // Some output folder names are also common source folders; look at the neighbours to tell.
        if (name.Equals("packages", StringComparison.OrdinalIgnoreCase))
        {
            // NuGet's legacy restore folder, or the workspaces of a JS monorepo.
            return JavaScriptWorkspaceMarkers.Any(siblingFiles.Contains);
        }

        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            // .NET build output, or executable scripts (bin/rails, bin/cli.dart, bin/cli.js).
            var isDotNetOutput = siblingFiles.Any(f => DotNetProjectExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                || Directory.Exists(Path.Combine(directory.FullName, "Debug"))
                || Directory.Exists(Path.Combine(directory.FullName, "Release"));
            return !isDotNetOutput;
        }

        if (name.Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            // Output of CMake, Gradle, Python packaging or JS bundlers, or a folder of build scripts.
            var isOutput = BuildOutputMarkers.Any(siblingFiles.Contains) || File.Exists(Path.Combine(directory.FullName, "CMakeCache.txt"));
            return !isOutput;
        }

        return false;
    }

    /// <summary>
    /// Symbolic links and junctions can point outside the project or create cycles. Other reparse
    /// points (OneDrive placeholders, deduplicated files) are regular content and must be kept.
    /// </summary>
    private static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads file sizes one directory listing at a time: one system call per folder instead of one
    /// per file, which matters for tens of thousands of files on Windows.
    /// </summary>
    private sealed class DirectorySizeCache(string root)
    {
        private readonly Dictionary<string, Dictionary<string, long>?> _directories = new(StringComparer.Ordinal);

        public long? TryGetLength(string relativePath)
        {
            var directory = RelativePaths.DirectoryOf(relativePath);
            if (!_directories.TryGetValue(directory, out var listing))
            {
                listing = List(directory);
                _directories[directory] = listing;
            }

            return listing is not null && listing.TryGetValue(RelativePaths.FileName(relativePath), out var length) ? length : null;
        }

        private Dictionary<string, long>? List(string relativeDirectory)
        {
            var full = relativeDirectory.Length == 0 ? root : Path.Combine(root, PathUtil.ToPlatform(relativeDirectory));
            try
            {
                var result = new Dictionary<string, long>(PathUtil.Comparer);
                foreach (var file in new DirectoryInfo(full).EnumerateFiles("*", ListingOptions))
                {
                    result.TryAdd(file.Name, SafeLength(file));
                }

                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
