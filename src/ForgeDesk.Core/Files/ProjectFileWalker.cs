using System.IO.Enumeration;

namespace ForgeDesk.Core.Files;

/// <summary>
/// Lazily enumerates the files of a project folder (relative paths, forward slashes) without
/// entering heavy folders or following directory links (which could loop or leave the project).
/// Unreadable folders are skipped.
/// </summary>
internal static class ProjectFileWalker
{
    public static IEnumerable<string> EnumerateFiles(string root)
    {
        var prefixLength = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root.Length
            : root.Length + 1;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        return new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => ToRelative(ref entry, prefixLength), options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => ShouldEnter(ref entry),
        };
    }

    private static string ToRelative(ref FileSystemEntry entry, int prefixLength)
    {
        var full = entry.ToFullPath();
        var relative = prefixLength < full.Length ? full[prefixLength..] : entry.FileName.ToString();
        return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool ShouldEnter(ref FileSystemEntry entry)
    {
        if (HeavyFolders.IsHeavy(entry.FileName))
        {
            return false;
        }

        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return true;
        }

        // Cloud placeholders (OneDrive) are reparse points too, but real folders: only skip actual links.
        try
        {
            return new DirectoryInfo(entry.ToFullPath()).LinkTarget is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
