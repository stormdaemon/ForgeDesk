namespace ForgeDesk.Presentation.Palette;

/// <summary>A palette item with its score and the characters of its title that matched.</summary>
public sealed record RankedPaletteItem(PaletteItem Item, double Score, IReadOnlyList<int> TitleIndices, int RecentRank)
{
    public bool IsRecent => RecentRank >= 0;
}

/// <summary>Filters, scores, de-duplicates and orders palette items for a query.</summary>
public static class PaletteRanker
{
    private const double TitleWeight = 1.0;
    private const double KeywordsWeight = 0.7;
    private const double SubtitleWeight = 0.5;
    private const double RecentBonus = 12;

    private static readonly char[] Separators = [' ', '\t'];

    public static IReadOnlyList<RankedPaletteItem> Rank(IEnumerable<PaletteItem> items, ParsedPaletteQuery query, PaletteRecents recents, int max)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(recents);

        var tokens = query.Term.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<(string, PaletteCategory, string?)>();
        var ranked = new List<RankedPaletteItem>();
        foreach (var item in items)
        {
            if (item is null || (query.Categories is { } categories && !categories.Contains(item.Category)))
            {
                continue;
            }

            if (!seen.Add((item.Title, item.Category, item.Subtitle)))
            {
                continue;
            }

            var recentRank = recents.RankOf(item);
            var recency = recentRank >= 0 ? RecentBonus * (PaletteRecents.Capacity - recentRank) / PaletteRecents.Capacity : 0;
            if (tokens.Length == 0)
            {
                ranked.Add(new RankedPaletteItem(item, item.Boost + recency, [], recentRank));
                continue;
            }

            if (Score(tokens, query.Term, item) is { } match)
            {
                ranked.Add(new RankedPaletteItem(item, match.Score + item.Boost + recency, match.TitleIndices, recentRank));
            }
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Category)
            .ThenBy(r => r.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, max))
            .ToList();
    }

    /// <summary>Every token must match the title, the keywords or the subtitle; the scores add up.</summary>
    private static (double Score, IReadOnlyList<int> TitleIndices)? Score(string[] tokens, string term, PaletteItem item)
    {
        // A multi-word query found as-is in the title ("open in explorer") beats scattered tokens.
        if (tokens.Length > 1 && FuzzyMatcher.Match(term, item.Title) is { } whole
            && item.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return (whole.Score * TitleWeight, whole.Indices);
        }

        var total = 0.0;
        var titleIndices = new SortedSet<int>();
        foreach (var token in tokens)
        {
            var title = FuzzyMatcher.Match(token, item.Title);
            var keywords = FuzzyMatcher.Match(token, item.Keywords);
            var subtitle = FuzzyMatcher.Match(token, item.Subtitle);

            var titleScore = title is null ? double.NegativeInfinity : title.Score * TitleWeight;
            var keywordScore = keywords is null ? double.NegativeInfinity : keywords.Score * KeywordsWeight;
            var subtitleScore = subtitle is null ? double.NegativeInfinity : subtitle.Score * SubtitleWeight;
            var best = Math.Max(titleScore, Math.Max(keywordScore, subtitleScore));
            if (double.IsNegativeInfinity(best))
            {
                return null;
            }

            total += best;
            if (title is not null && titleScore >= best)
            {
                titleIndices.UnionWith(title.Indices);
            }
        }

        return (total, titleIndices.ToArray());
    }
}
