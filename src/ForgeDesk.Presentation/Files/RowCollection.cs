using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Files;

/// <summary>
/// The visible rows of a virtualized list. Small changes are applied item by item (the list keeps
/// its containers, scroll position and selection); large ones (expanding a folder of 20 000 files,
/// switching modes) replace the content with a single Reset notification instead of thousands.
/// </summary>
public sealed class RowCollection<T> : ObservableCollection<T>
    where T : class
{
    /// <summary>Above this many inserted or removed rows, the list is reset in one notification.</summary>
    public const int BulkThreshold = 256;

    public void Update(IReadOnlyList<T> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (Math.Abs(Count - desired.Count) > BulkThreshold || (Count > BulkThreshold * 4 && desired.Count > BulkThreshold * 4))
        {
            if (!SequenceEqual(desired))
            {
                ReplaceAll(desired);
            }

            return;
        }

        this.SyncWith(desired);
    }

    /// <summary>Replaces every item, raising one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private bool SequenceEqual(IReadOnlyList<T> desired)
    {
        if (desired.Count != Count)
        {
            return false;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (!ReferenceEquals(Items[i], desired[i]))
            {
                return false;
            }
        }

        return true;
    }
}
