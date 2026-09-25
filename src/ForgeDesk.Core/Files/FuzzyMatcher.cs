using System.Buffers;

namespace ForgeDesk.Core.Files;

/// <summary>
/// fzf-style fuzzy matching over a fixed list of paths, built once per index snapshot.
/// <para>
/// A query matches a path when its characters appear in order. Scores reward matches at segment
/// starts (after "/", "_", "-", ".", camelCase humps), consecutive runs, matches inside the file
/// name, and exact file-name hits; gaps cost points. Searching runs in two phases: a cheap pass
/// over every path (character-bag rejection, greedy alignment) keeps the best few hundred
/// candidates, then an exact dynamic-programming alignment re-scores those and yields the
/// highlighted positions.
/// </para>
/// All paths are lowercased once into one contiguous buffer so the hot loop is allocation-free.
/// </summary>
internal sealed class FuzzyMatcher
{
    internal const int ScoreMatch = 16;
    internal const int GapStart = -3;
    internal const int GapExtension = -1;
    internal const int BonusSegmentStart = 10;
    internal const int BonusBoundary = 8;
    internal const int BonusCamel = 7;
    internal const int BonusConsecutive = 5;
    internal const int FirstCharMultiplier = 2;
    internal const int BonusFileName = 6;
    internal const int BonusExactFileName = 60;
    internal const int BonusExactStem = 45;
    internal const int BonusFileNamePrefix = 20;

    private const int NoMatch = int.MinValue;
    private const int MaxAlignedLength = 1024;
    private const int MaxQueryLength = 128;
    private const int MostRelevantCacheSize = 1000;

    private readonly string[] _paths;
    private readonly char[] _lower;
    private readonly int[] _starts;
    private readonly int[] _nameStarts;
    private readonly ulong[] _masks;
    private int[]? _mostRelevant;

    private FuzzyMatcher(string[] paths, char[] lower, int[] starts, int[] nameStarts, ulong[] masks)
    {
        _paths = paths;
        _lower = lower;
        _starts = starts;
        _nameStarts = nameStarts;
        _masks = masks;
    }

    public int Count => _paths.Length;

    public static FuzzyMatcher Create(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths as string[] ?? [.. paths];
        long total = 0;
        foreach (var path in copy)
        {
            total += path.Length;
        }

        var lower = new char[total];
        var starts = new int[copy.Length + 1];
        var nameStarts = new int[copy.Length];
        var masks = new ulong[copy.Length];
        var offset = 0;
        for (var i = 0; i < copy.Length; i++)
        {
            var path = copy[i];
            starts[i] = offset;
            nameStarts[i] = path.LastIndexOf('/') + 1;
            ulong mask = 0;
            for (var j = 0; j < path.Length; j++)
            {
                var c = ToLower(path[j]);
                lower[offset + j] = c;
                mask |= MaskBit(c);
            }

            masks[i] = mask;
            offset += path.Length;
        }

        starts[copy.Length] = offset;
        return new FuzzyMatcher(copy, lower, starts, nameStarts, masks);
    }

    public IReadOnlyList<FileMatch> Search(string? query, int max)
    {
        if (max <= 0 || _paths.Length == 0)
        {
            return [];
        }

        var pattern = NormalizeQuery(query);
        if (pattern.Length == 0)
        {
            return MostRelevant(max);
        }

        ulong queryMask = 0;
        foreach (var c in pattern)
        {
            queryMask |= MaskBit(c);
        }

        // Phase 1: every path, cheap score, keep the best candidates.
        var candidates = new TopCandidates(Math.Clamp(max * 4, 64, 1000), this);
        for (var i = 0; i < _paths.Length; i++)
        {
            if ((_masks[i] & queryMask) != queryMask)
            {
                continue;
            }

            var score = QuickScore(i, pattern);
            if (score != NoMatch)
            {
                candidates.Offer(score, i);
            }
        }

        // Phase 2: exact alignment of the survivors.
        var scored = new List<(int Score, int Index, int[] Positions)>(candidates.Count);
        foreach (var index in candidates.Indexes())
        {
            var positions = Align(index, pattern, out var score);
            scored.Add((score + FileNameBonus(index, pattern), index, positions));
        }

        scored.Sort((a, b) => Compare(b.Score, b.Index, a.Score, a.Index));
        var count = Math.Min(max, scored.Count);
        var results = new FileMatch[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new FileMatch(_paths[scored[i].Index], scored[i].Score, scored[i].Positions);
        }

        return results;
    }

    /// <summary>Higher score first, then shorter path, then ordinal: positive when (scoreA, a) ranks above (scoreB, b).</summary>
    private int Compare(int scoreA, int a, int scoreB, int b)
    {
        if (scoreA != scoreB)
        {
            return scoreA.CompareTo(scoreB);
        }

        var lengthA = _starts[a + 1] - _starts[a];
        var lengthB = _starts[b + 1] - _starts[b];
        if (lengthA != lengthB)
        {
            return lengthB.CompareTo(lengthA);
        }

        return string.CompareOrdinal(_paths[b], _paths[a]);
    }

    internal static char[] NormalizeQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var buffer = new List<char>(Math.Min(query.Length, MaxQueryLength));
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            buffer.Add(c == '\\' ? '/' : ToLower(c));
            if (buffer.Count == MaxQueryLength)
            {
                break;
            }
        }

        return [.. buffer];
    }

    /// <summary>Empty query: shallow, short paths first (the project's top-level files).</summary>
    private FileMatch[] MostRelevant(int max)
    {
        var order = _mostRelevant ??= ComputeMostRelevant();
        var count = Math.Min(max, order.Length);
        var results = new FileMatch[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new FileMatch(_paths[order[i]], 0, []);
        }

        return results;
    }

    private int[] ComputeMostRelevant()
    {
        var keys = new long[_paths.Length];
        var indexes = new int[_paths.Length];
        for (var i = 0; i < _paths.Length; i++)
        {
            var path = _paths[i];
            var depth = path.AsSpan().Count('/');
            keys[i] = ((long)depth << 32) | (uint)path.Length;
            indexes[i] = i;
        }

        Array.Sort(keys, indexes);
        var take = Math.Min(MostRelevantCacheSize, indexes.Length);
        var top = new (long Key, int Index)[take];
        for (var i = 0; i < take; i++)
        {
            top[i] = (keys[i], indexes[i]);
        }

        // Deterministic, readable order among equal depth and length.
        Array.Sort(top, (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.CompareOrdinal(_paths[a.Index], _paths[b.Index]));
        return Array.ConvertAll(top, t => t.Index);
    }

    /// <summary>Phase 1: greedy alignment (file name first, then the whole path) scored with the real model.</summary>
    private int QuickScore(int index, char[] pattern)
    {
        var start = _starts[index];
        var text = _lower.AsSpan(start, _starts[index + 1] - start);
        if (!FindWindow(text, 0, pattern, out var windowStart, out var windowEnd))
        {
            return NoMatch;
        }

        var nameStart = _nameStarts[index];
        if (text.Length - nameStart >= pattern.Length && FindWindow(text, nameStart, pattern, out var nameWindowStart, out var nameWindowEnd))
        {
            windowStart = nameWindowStart;
            windowEnd = nameWindowEnd;
        }

        return ScoreGreedy(index, text, windowStart, windowEnd, pattern) + FileNameBonus(index, pattern);
    }

    /// <summary>
    /// fzf v1: the first window [start, end) of <paramref name="text"/> (from <paramref name="from"/>)
    /// containing the pattern as a subsequence, shrunk from the left.
    /// </summary>
    private static bool FindWindow(ReadOnlySpan<char> text, int from, char[] pattern, out int start, out int end)
    {
        start = -1;
        end = -1;
        var p = 0;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == pattern[p])
            {
                if (++p == pattern.Length)
                {
                    end = i + 1;
                    break;
                }
            }
        }

        if (end < 0)
        {
            return false;
        }

        p = pattern.Length - 1;
        for (var i = end - 1; i >= from; i--)
        {
            if (text[i] == pattern[p] && --p < 0)
            {
                start = i;
                break;
            }
        }

        return true;
    }

    private int ScoreGreedy(int index, ReadOnlySpan<char> text, int windowStart, int windowEnd, char[] pattern)
    {
        var original = _paths[index];
        var nameStart = _nameStarts[index];
        var score = 0;
        var previous = -1;
        var firstBonus = 0;
        var p = 0;
        for (var j = windowStart; j < windowEnd && p < pattern.Length; j++)
        {
            if (text[j] != pattern[p])
            {
                continue;
            }

            var bonus = Bonus(original, j);
            if (previous >= 0 && j == previous + 1)
            {
                if (bonus >= BonusBoundary && bonus > firstBonus)
                {
                    firstBonus = bonus;
                }

                bonus = Math.Max(Math.Max(bonus, firstBonus), BonusConsecutive);
            }
            else
            {
                if (previous >= 0)
                {
                    score += GapStart + ((j - previous - 2) * GapExtension);
                }

                firstBonus = bonus;
            }

            score += ScoreMatch + (p == 0 ? bonus * FirstCharMultiplier : bonus) + (j >= nameStart ? BonusFileName : 0);
            previous = j;
            p++;
        }

        return score;
    }

    /// <summary>Phase 2: optimal alignment (Smith-Waterman-like, as fzf v2) and its positions.</summary>
    private int[] Align(int index, char[] pattern, out int score)
    {
        var start = _starts[index];
        var n = _starts[index + 1] - start;
        var m = pattern.Length;
        var text = _lower.AsSpan(start, n);
        if (n > MaxAlignedLength)
        {
            return GreedyPositions(index, text, pattern, out score);
        }

        var original = _paths[index];
        var nameStart = _nameStarts[index];
        var size = m * n;
        var h = ArrayPool<int>.Shared.Rent(size);
        var firstBonus = ArrayPool<int>.Shared.Rent(size);
        var from = ArrayPool<int>.Shared.Rent(size);
        var bonuses = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (var j = 0; j < n; j++)
            {
                bonuses[j] = Bonus(original, j);
            }

            for (var i = 0; i < m; i++)
            {
                var row = i * n;
                var previousRow = row - n;
                var runBest = NoMatch;
                var runBestFrom = -1;
                var q = pattern[i];
                for (var j = 0; j < n; j++)
                {
                    h[row + j] = NoMatch;
                    if (i > 0)
                    {
                        // Best predecessor k <= j - 2 (a gap of j - k - 1 characters).
                        if (runBest != NoMatch)
                        {
                            runBest += GapExtension;
                        }

                        if (j >= 2 && h[previousRow + j - 2] != NoMatch && h[previousRow + j - 2] + GapStart > runBest)
                        {
                            runBest = h[previousRow + j - 2] + GapStart;
                            runBestFrom = j - 2;
                        }
                    }

                    if (text[j] != q || j < i || n - j < m - i)
                    {
                        continue;
                    }

                    var bonus = bonuses[j];
                    var nameBonus = j >= nameStart ? BonusFileName : 0;
                    int best;
                    int bestFrom;
                    int bestFirstBonus;
                    if (i == 0)
                    {
                        best = ScoreMatch + (bonus * FirstCharMultiplier) + nameBonus;
                        bestFrom = -1;
                        bestFirstBonus = bonus;
                    }
                    else
                    {
                        best = NoMatch;
                        bestFrom = -1;
                        bestFirstBonus = bonus;
                        if (runBest != NoMatch)
                        {
                            best = runBest + ScoreMatch + bonus + nameBonus;
                            bestFrom = runBestFrom;
                        }

                        var diagonal = j > 0 ? h[previousRow + j - 1] : NoMatch;
                        if (diagonal != NoMatch)
                        {
                            var chunkBonus = firstBonus[previousRow + j - 1];
                            if (bonus >= BonusBoundary && bonus > chunkBonus)
                            {
                                chunkBonus = bonus;
                            }

                            var consecutive = diagonal + ScoreMatch + Math.Max(Math.Max(bonus, chunkBonus), BonusConsecutive) + nameBonus;
                            if (consecutive >= best)
                            {
                                best = consecutive;
                                bestFrom = j - 1;
                                bestFirstBonus = chunkBonus;
                            }
                        }

                        if (best == NoMatch)
                        {
                            continue;
                        }
                    }

                    h[row + j] = best;
                    from[row + j] = bestFrom;
                    firstBonus[row + j] = bestFirstBonus;
                }
            }

            var lastRow = (m - 1) * n;
            var end = -1;
            score = NoMatch;
            for (var j = m - 1; j < n; j++)
            {
                if (h[lastRow + j] != NoMatch && h[lastRow + j] > score)
                {
                    score = h[lastRow + j];
                    end = j;
                }
            }

            if (end < 0)
            {
                return GreedyPositions(index, text, pattern, out score);
            }

            var positions = new int[m];
            for (int i = m - 1, j = end; i >= 0; i--)
            {
                positions[i] = j;
                j = from[(i * n) + j];
            }

            return positions;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(h);
            ArrayPool<int>.Shared.Return(firstBonus);
            ArrayPool<int>.Shared.Return(from);
            ArrayPool<int>.Shared.Return(bonuses);
        }
    }

    private int[] GreedyPositions(int index, ReadOnlySpan<char> text, char[] pattern, out int score)
    {
        FindWindow(text, 0, pattern, out var windowStart, out var windowEnd);
        var nameStart = _nameStarts[index];
        if (text.Length - nameStart >= pattern.Length && FindWindow(text, nameStart, pattern, out var nameWindowStart, out var nameWindowEnd))
        {
            windowStart = nameWindowStart;
            windowEnd = nameWindowEnd;
        }

        score = ScoreGreedy(index, text, windowStart, windowEnd, pattern);
        var positions = new int[pattern.Length];
        var p = 0;
        for (var j = windowStart; j < windowEnd && p < pattern.Length; j++)
        {
            if (text[j] == pattern[p])
            {
                positions[p++] = j;
            }
        }

        return positions;
    }

    /// <summary>Extra points when the query is the file name, its stem, or its beginning.</summary>
    private int FileNameBonus(int index, char[] pattern)
    {
        var start = _starts[index];
        var nameStart = _nameStarts[index];
        var name = _lower.AsSpan(start + nameStart, _starts[index + 1] - start - nameStart);
        var query = pattern.AsSpan();
        if (name.SequenceEqual(query))
        {
            return BonusExactFileName;
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0 && name[..dot].SequenceEqual(query))
        {
            return BonusExactStem;
        }

        return name.StartsWith(query) ? BonusFileNamePrefix : 0;
    }

    private static int Bonus(string path, int j)
    {
        if (j == 0)
        {
            return BonusSegmentStart;
        }

        var previous = path[j - 1];
        var current = path[j];
        if (previous is '/' or '\\')
        {
            return BonusSegmentStart;
        }

        if (!char.IsLetterOrDigit(previous))
        {
            return char.IsLetterOrDigit(current) ? BonusBoundary : 0;
        }

        if (char.IsLower(previous) && char.IsUpper(current))
        {
            return BonusCamel;
        }

        return !char.IsDigit(previous) && char.IsDigit(current) ? BonusCamel : 0;
    }

    private static char ToLower(char c) =>
        c < 128 ? (char)(c is >= 'A' and <= 'Z' ? c | 0x20 : c) : char.ToLowerInvariant(c);

    private static ulong MaskBit(char c) => c switch
    {
        >= 'a' and <= 'z' => 1UL << (c - 'a'),
        >= '0' and <= '9' => 1UL << (26 + c - '0'),
        '.' => 1UL << 36,
        '_' => 1UL << 37,
        '-' => 1UL << 38,
        '/' => 1UL << 39,
        _ => 1UL << (40 + (c % 24)),
    };

    /// <summary>Bounded min-heap keeping the best phase-1 candidates.</summary>
    private sealed class TopCandidates(int capacity, FuzzyMatcher owner)
    {
        private readonly int[] _scores = new int[capacity];
        private readonly int[] _indexes = new int[capacity];

        public int Count { get; private set; }

        public void Offer(int score, int index)
        {
            if (Count < capacity)
            {
                _scores[Count] = score;
                _indexes[Count] = index;
                SiftUp(Count++);
                return;
            }

            // Replace the weakest candidate only when the newcomer ranks above it.
            if (owner.Compare(score, index, _scores[0], _indexes[0]) <= 0)
            {
                return;
            }

            _scores[0] = score;
            _indexes[0] = index;
            SiftDown(0);
        }

        public IEnumerable<int> Indexes() => _indexes.Take(Count);

        private bool Lower(int a, int b) => owner.Compare(_scores[a], _indexes[a], _scores[b], _indexes[b]) < 0;

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (!Lower(i, parent))
                {
                    break;
                }

                Swap(i, parent);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                var left = (2 * i) + 1;
                if (left >= Count)
                {
                    return;
                }

                var smallest = left + 1 < Count && Lower(left + 1, left) ? left + 1 : left;
                if (!Lower(smallest, i))
                {
                    return;
                }

                Swap(i, smallest);
                i = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
            (_indexes[a], _indexes[b]) = (_indexes[b], _indexes[a]);
        }
    }
}
