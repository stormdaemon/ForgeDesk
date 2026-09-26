namespace ForgeDesk.Presentation.Dashboard;

/// <summary>Sorting and grouping rules of the dashboard.</summary>
public static class DashboardOrdering
{
    /// <summary>Splits a search box text into terms; every term must match.</summary>
    public static IReadOnlyList<string> SearchTerms(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Group headers are shown when sorting by name or recency and at least one project has a group.</summary>
    public static bool ShouldGroup(DashboardSortMode mode, IEnumerable<ProjectCardViewModel> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return mode is DashboardSortMode.Name or DashboardSortMode.Recent && cards.Any(c => c.HasGroup);
    }

    /// <summary>
    /// Orders <paramref name="cards"/> for display. Grouped: named groups alphabetically, then the
    /// projects without a group, each group sorted by <paramref name="mode"/>.
    /// </summary>
    public static IReadOnlyList<ProjectCardViewModel> Order(IEnumerable<ProjectCardViewModel> cards, DashboardSortMode mode, bool grouped)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var names = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ProjectCardViewModel> ordered;
        if (grouped)
        {
            ordered = cards.OrderBy(c => c.HasGroup ? 0 : 1).ThenBy(c => c.Group ?? string.Empty, names);
            ordered = mode switch
            {
                DashboardSortMode.Recent => ThenByRecent(ordered),
                DashboardSortMode.Attention => ThenByAttention(ordered),
                _ => ordered.ThenBy(c => c.Name, names),
            };
        }
        else
        {
            ordered = mode switch
            {
                DashboardSortMode.Recent => cards.OrderByDescending(c => c.LastOpenedAt.HasValue).ThenByDescending(c => c.LastOpenedAt ?? c.AddedAt),
                DashboardSortMode.Attention => cards.OrderByDescending(c => c.AttentionRank)
                    .ThenByDescending(c => c.IsRunning)
                    .ThenByDescending(c => c.LastOpenedAt ?? c.AddedAt),
                _ => cards.OrderBy(c => c.Name, names),
            };
        }

        return ordered.ThenBy(c => c.Name, names).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    private static IOrderedEnumerable<ProjectCardViewModel> ThenByRecent(IOrderedEnumerable<ProjectCardViewModel> ordered) =>
        ordered.ThenByDescending(c => c.LastOpenedAt.HasValue).ThenByDescending(c => c.LastOpenedAt ?? c.AddedAt);

    private static IOrderedEnumerable<ProjectCardViewModel> ThenByAttention(IOrderedEnumerable<ProjectCardViewModel> ordered) =>
        ordered.ThenByDescending(c => c.AttentionRank).ThenByDescending(c => c.IsRunning).ThenByDescending(c => c.LastOpenedAt ?? c.AddedAt);
}
