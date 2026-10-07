using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;

namespace ForgeDesk.Presentation.Files;

/// <summary>
/// Pure tree logic of the Files tab: merging directory listings into existing nodes (so expansion
/// and selection survive refreshes) and flattening the expanded tree into visible rows.
/// </summary>
public static class FileTree
{
    /// <summary>
    /// Builds the children of a folder from a fresh listing, reusing the nodes of
    /// <paramref name="existing"/> with the same path (their expansion and loaded children are kept).
    /// Returns null when the listing is identical to the current children (nothing to update).
    /// </summary>
    public static List<FileTreeNodeViewModel>? Merge(IReadOnlyList<FileTreeNodeViewModel>? existing, IReadOnlyList<FileEntry> entries, int depth)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byPath = new Dictionary<string, FileTreeNodeViewModel>(PathUtil.Comparer);
        if (existing is not null)
        {
            foreach (var node in existing)
            {
                byPath.TryAdd(node.RelativePath, node);
            }
        }

        var changed = existing is null || existing.Count != entries.Count;
        var result = new List<FileTreeNodeViewModel>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var path = entry.RelativePath.Replace('\\', '/');
            if (byPath.TryGetValue(path, out var node) && node.IsDirectory == entry.IsDirectory)
            {
                if (!changed && (!ReferenceEquals(existing![i], node) || HasEntryChanged(node, entry)))
                {
                    changed = true;
                }

                node.Update(entry);
            }
            else
            {
                node = new FileTreeNodeViewModel(entry, depth);
                changed = true;
            }

            result.Add(node);
        }

        return changed ? result : null;
    }

    /// <summary>
    /// The visible rows: depth-first over <paramref name="roots"/>, descending into expanded,
    /// loaded folders. <paramref name="include"/> filters nodes (hidden files…); an excluded folder
    /// hides its content too.
    /// </summary>
    public static List<FileTreeNodeViewModel> Flatten(IEnumerable<FileTreeNodeViewModel> roots, Func<FileTreeNodeViewModel, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var rows = new List<FileTreeNodeViewModel>();
        var stack = new Stack<IEnumerator<FileTreeNodeViewModel>>();
        stack.Push(roots.GetEnumerator());
        while (stack.Count > 0)
        {
            var current = stack.Peek();
            if (!current.MoveNext())
            {
                current.Dispose();
                stack.Pop();
                continue;
            }

            var node = current.Current;
            if (include is not null && !include(node))
            {
                continue;
            }

            rows.Add(node);
            if (node.IsDirectory && node.IsExpanded && node.Children is { Count: > 0 } children)
            {
                stack.Push(children.GetEnumerator());
            }
        }

        return rows;
    }

    /// <summary>Every loaded folder below <paramref name="roots"/> (depth-first), expanded or not.</summary>
    public static IEnumerable<FileTreeNodeViewModel> LoadedFolders(IEnumerable<FileTreeNodeViewModel> roots)
    {
        foreach (var node in roots)
        {
            if (!node.IsDirectory || node.Children is null)
            {
                continue;
            }

            yield return node;
            foreach (var child in LoadedFolders(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>Every loaded node below <paramref name="roots"/>.</summary>
    public static IEnumerable<FileTreeNodeViewModel> AllLoaded(IEnumerable<FileTreeNodeViewModel> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            if (node.Children is { } children)
            {
                foreach (var child in AllLoaded(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool HasEntryChanged(FileTreeNodeViewModel node, FileEntry entry) =>
        node.Size != entry.Size
        || node.LastModified != entry.LastModified
        || node.IsHidden != entry.IsHidden
        || node.IsIgnored != entry.IsIgnored
        || node.IsHeavy != entry.IsHeavyFolder
        || !string.Equals(node.Name, entry.Name, StringComparison.Ordinal);
}
