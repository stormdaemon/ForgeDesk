namespace ForgeDesk.Core.WorkItems;

/// <summary>
/// Fractional ordering within a board column: an item dropped between two neighbours gets the
/// midpoint of their sort orders, so a move rewrites a single row. When repeated insertions at
/// the same spot exhaust double precision, the column is renumbered (see <see cref="Renumber"/>).
/// </summary>
internal static class SortOrderMath
{
    public const double Step = 1024;

    /// <summary>Below this gap the midpoint is no longer reliably distinct from its neighbours.</summary>
    public const double MinimumGap = 1e-6;

    /// <summary>Sort order between two neighbours (null = column edge), or null when the gap is too small.</summary>
    public static double? Between(double? previous, double? next)
    {
        switch (previous, next)
        {
            case (null, null):
                return Step;
            case (null, { } n):
                return n - Step;
            case ({ } p, null):
                return p + Step;
            case ({ } p, { } n):
                if (n - p < MinimumGap)
                {
                    return null;
                }

                var middle = p + ((n - p) / 2);
                return middle > p && middle < n ? middle : null;
        }
    }

    /// <summary>Evenly spaced sort order of the item at <paramref name="position"/> (0-based) of a renumbered column.</summary>
    public static double Renumber(int position) => (position + 1) * Step;
}
