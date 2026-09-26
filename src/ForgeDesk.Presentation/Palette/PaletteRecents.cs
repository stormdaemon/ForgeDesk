namespace ForgeDesk.Presentation.Palette;

/// <summary>The last palette entries the user executed, identified by title and category (in memory, per session).</summary>
public sealed class PaletteRecents
{
    public const int Capacity = 10;

    private readonly List<(string Title, PaletteCategory Category)> _entries = [];

    public int Count => _entries.Count;

    public void Remember(PaletteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = (item.Title, item.Category);
        _entries.Remove(key);
        _entries.Insert(0, key);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
    }

    /// <summary>0 for the most recently executed entry, -1 when the item was not executed recently.</summary>
    public int RankOf(PaletteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _entries.IndexOf((item.Title, item.Category));
    }
}
