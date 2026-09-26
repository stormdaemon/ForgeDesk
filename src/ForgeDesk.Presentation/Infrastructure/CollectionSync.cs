using System.Collections.ObjectModel;

namespace ForgeDesk.Presentation.Infrastructure;

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> contain exactly <paramref name="desired"/>, in order, with
    /// the fewest moves, inserts and removals, so bound lists keep their scroll position,
    /// selection and item containers.
    /// </summary>
    public static void SyncWith<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                continue;
            }

            var existing = IndexOf(target, item, i + 1);
            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start)
        where T : class
    {
        for (var i = start; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
