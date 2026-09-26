namespace ForgeDesk.Core.Projects;

/// <summary>
/// Recursive deletion that works on git's read-only object files (Windows refuses to delete
/// them otherwise) and never follows symbolic links or junctions out of the tree.
/// </summary>
internal static class SafeDelete
{
    public static void Tree(FileSystemInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (IsLink(root) || root is FileInfo)
        {
            DeleteEntry(root);
            return;
        }

        var directories = new Stack<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push((DirectoryInfo)root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories.Push(directory);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo subdirectory && !IsLink(entry))
                {
                    pending.Push(subdirectory);
                }
                else
                {
                    DeleteEntry(entry);
                }
            }
        }

        // Children were pushed after their parents, so they are deleted first.
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
            {
                directory.Attributes &= ~FileAttributes.ReadOnly;
            }

            directory.Delete(recursive: false);
        }
    }

    private static void DeleteEntry(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory)
        {
            // A link to a directory: remove the link itself, not its target.
            directory.Delete(recursive: false);
            return;
        }

        // Changing attributes through a link would change its target (chmod follows links).
        if (!IsLink(entry) && (entry.Attributes & FileAttributes.ReadOnly) != 0)
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
        }

        entry.Delete();
    }

    private static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null;
}
