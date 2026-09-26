using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

/// <summary>File-system operations on a working tree, confined to the repository folder.</summary>
internal static class WorkingTreeFiles
{
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>True when <paramref name="relativePath"/> is a folder holding its own repository (never deleted as "untracked").</summary>
    public static bool IsNestedRepository(string root, string relativePath)
    {
        var full = PathUtil.ResolveUnder(root, relativePath);
        return Directory.Exists(full) && !IsLink(full) && Path.Exists(Path.Combine(full, ".git"));
    }

    /// <summary>
    /// Deletes an untracked file, link or folder of the working tree, then the folders its removal left
    /// empty (git doesn't track folders, so they would otherwise linger).
    /// </summary>
    public static void DeleteUntracked(string root, string relativePath)
    {
        var full = PathUtil.ResolveUnder(root, relativePath);
        if (PathUtil.AreSame(full, root))
        {
            throw ForgeException.InvalidInput("The repository folder itself can't be discarded.");
        }

        if (IsLink(full))
        {
            // Remove the link only, never what it points to.
            if (Directory.Exists(full))
            {
                Directory.Delete(full);
            }
            else
            {
                File.Delete(full);
            }
        }
        else if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            ClearReadOnly(full);
            Directory.Delete(full, recursive: true);
        }
        else
        {
            return;
        }

        RemoveEmptyParents(root, Path.GetDirectoryName(full));
    }

    /// <summary>
    /// Removes what a failed or cancelled clone left behind. Git marks pack files read-only, and on
    /// Windows a just-killed process may hold handles for a moment, hence the attribute reset and retries.
    /// </summary>
    public static async Task RemoveCloneLeftoversAsync(string target, bool keepFolder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(target))
                {
                    return;
                }

                ClearReadOnly(target);
                if (keepFolder)
                {
                    foreach (var entry in new DirectoryInfo(target).EnumerateFileSystemInfos())
                    {
                        if (entry is DirectoryInfo directory && !IsLink(directory.FullName))
                        {
                            directory.Delete(recursive: true);
                        }
                        else
                        {
                            entry.Delete();
                        }
                    }
                }
                else
                {
                    Directory.Delete(target, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < DeleteAttempts)
            {
                await Task.Delay(RetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the clone error is what the user needs to see, not this one.
                System.Diagnostics.Trace.TraceWarning($"Could not remove the partial clone at {target}: {ex.Message}");
                return;
            }
        }
    }

    private static void RemoveEmptyParents(string root, string? directory)
    {
        var normalizedRoot = PathUtil.Normalize(root);
        while (directory is not null
               && PathUtil.IsWithin(normalizedRoot, directory)
               && !PathUtil.AreSame(normalizedRoot, directory)
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void ClearReadOnly(string directory)
    {
        // Hidden entries (.git on Windows) included; links skipped so nothing outside the folder is touched.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static bool IsLink(string path) =>
        new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
}
