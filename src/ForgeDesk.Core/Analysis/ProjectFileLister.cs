using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Analysis;

/// <summary>A project file: git-style relative path and size.</summary>
internal sealed record ProjectFile(string RelativePath, long Bytes);

internal sealed record FileListing(IReadOnlyList<ProjectFile> Files, bool Truncated, bool FromGit);

/// <summary>
/// Lists the files of a project: <c>git ls-files</c> in repositories (so .gitignore is honored),
/// otherwise a bounded breadth-first walk that skips heavy folders and never follows links.
/// </summary>
internal sealed class ProjectFileLister(IGitService git, ILogger logger)
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,

        // Dot-folders are "hidden" on Unix and must be listed (.github); junctions and symlinks can loop.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public async Task<FileListing> ListAsync(string root, bool isRepository, CancellationToken cancellationToken)
    {
        if (isRepository)
        {
            try
            {
                var paths = await git.ListFilesAsync(root, cancellationToken).ConfigureAwait(false);
                return FromGit(root, paths, cancellationToken);
            }
            catch (ForgeException ex)
            {
                logger.LogInformation(ex, "git ls-files failed in {Root}; walking the folder instead", root);
            }
        }

        return Walk(root, cancellationToken);
    }

    internal static FileListing FromGit(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken, int maxFiles = AnalysisLimits.MaxListedFiles)
    {
        var files = new List<ProjectFile>(Math.Min(paths.Count, maxFiles));
        var seen = new HashSet<string>(PathUtil.Comparer);
        var truncated = false;

        // Git sorts root files after many folders; when the listing is capped, the root files
        // (README, LICENSE, manifests) matter most, so they go first.
        foreach (var raw in paths.OrderBy(p => p.Contains('/', StringComparison.Ordinal) || p.Contains('\\', StringComparison.Ordinal) ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = raw.Replace('\\', '/');
            if (relative.Length == 0 || HeavyFolders.IsInNeverSourceFolder(relative) || !seen.Add(relative))
            {
                continue;
            }

            if (files.Count >= maxFiles)
            {
                truncated = true;
                break;
            }

            // Tracked files deleted from the working tree are still listed by git.
            var info = new FileInfo(Path.Combine(root, PathUtil.ToPlatform(relative)));
            if (info.Exists)
            {
                files.Add(new ProjectFile(relative, info.Length));
            }
        }

        return new FileListing(files, truncated, FromGit: true);
    }

    internal static FileListing Walk(string root, CancellationToken cancellationToken, int maxFiles = AnalysisLimits.MaxListedFiles)
    {
        var files = new List<ProjectFile>();
        var pending = new Queue<DirectoryInfo>();
        var rootInfo = new DirectoryInfo(root);
        pending.Enqueue(rootInfo);

        // Breadth-first, so a truncated listing still contains the top-level files that matter most.
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. directory.EnumerateFileSystemInfos("*", Options).OrderBy(e => e.Name, StringComparer.Ordinal)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (!HeavyFolders.IsHeavy(child.Name))
                    {
                        pending.Enqueue(child);
                    }

                    continue;
                }

                if (files.Count >= maxFiles)
                {
                    return new FileListing(files, Truncated: true, FromGit: false);
                }

                var relative = Path.GetRelativePath(rootInfo.FullName, entry.FullName).Replace('\\', '/');
                files.Add(new ProjectFile(relative, ((FileInfo)entry).Length));
            }
        }

        return new FileListing(files, Truncated: false, FromGit: false);
    }
}
