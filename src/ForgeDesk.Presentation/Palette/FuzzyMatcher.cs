using System.Buffers;

namespace ForgeDesk.Presentation.Palette;

/// <summary>A successful fuzzy match: its score (higher is better) and the matched character positions.</summary>
public sealed record FuzzyMatch(double Score, IReadOnlyList<int> Indices);

/// <summary>
/// Subsequence matcher in the spirit of fzf / VS Code quick open: every pattern character must
/// appear in order; contiguous runs, word starts ("git push" → <b>G</b>it <b>p</b>ush), camelCase
/// humps and early matches score higher, gaps cost points. Case-insensitive.
/// </summary>
public static class FuzzyMatcher
{
    private const int MaxCandidateLength = 256;
    private const int MaxPatternLength = 64;
    private const double MatchScore = 16;
    private const double FirstCharacterBonus = 10;
    private const double WordStartBonus = 8;
    private const double CamelCaseBonus = 7;
    private const double ConsecutiveBonus = 5;
    private const double GapStartPenalty = 2;
    private const double GapPenalty = 1;
    private const double LeadingGapPenalty = 0.2;
    private const double MaxLeadingGapPenalty = 5;
    private const double ExactMatchBonus = 30;
    private const double PrefixBonus = 15;

    /// <summary>Scores <paramref name="candidate"/> against <paramref name="pattern"/>; null when it does not match.</summary>
    public static FuzzyMatch? Match(string pattern, string? candidate)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (string.IsNullOrEmpty(candidate) || pattern.Length == 0)
        {
            return null;
        }

        var p = pattern.Length > MaxPatternLength ? pattern[..MaxPatternLength] : pattern;
        var c = candidate.Length > MaxCandidateLength ? candidate[..MaxCandidateLength] : candidate;
        var n = p.Length;
        var m = c.Length;
        if (n > m || !IsSubsequence(p, c))
        {
            return null;
        }

        var scores = ArrayPool<double>.Shared.Rent(n * m);
        var previous = ArrayPool<int>.Shared.Rent(n * m);
        try
        {
            Fill(p, c, scores, previous);

            var bestEnd = -1;
            var best = double.NegativeInfinity;
            for (var j = n - 1; j < m; j++)
            {
                var value = scores[((n - 1) * m) + j];
                if (value > best)
                {
                    best = value;
                    bestEnd = j;
                }
            }

            if (bestEnd < 0 || double.IsNegativeInfinity(best))
            {
                return null;
            }

            var indices = new int[n];
            var column = bestEnd;
            for (var i = n - 1; i >= 0; i--)
            {
                indices[i] = column;
                column = previous[(i * m) + column];
            }

            if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase))
            {
                best += ExactMatchBonus;
            }
            else if (c.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                best += PrefixBonus;
            }

            return new FuzzyMatch(best, indices);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scores);
            ArrayPool<int>.Shared.Return(previous);
        }
    }

    private static void Fill(string pattern, string candidate, double[] scores, int[] previous)
    {
        var n = pattern.Length;
        var m = candidate.Length;
        for (var i = 0; i < n; i++)
        {
            var pc = char.ToLowerInvariant(pattern[i]);

            // Best "S[i-1,k] + GapPenalty * k" over k <= j-2 (a predecessor leaving a gap), updated as j grows.
            var runningBest = double.NegativeInfinity;
            var runningIndex = -1;
            for (var j = 0; j < m; j++)
            {
                var cell = (i * m) + j;
                if (i > 0 && j >= 2)
                {
                    var candidateScore = scores[((i - 1) * m) + j - 2];
                    if (!double.IsNegativeInfinity(candidateScore) && candidateScore + (GapPenalty * (j - 2)) > runningBest)
                    {
                        runningBest = candidateScore + (GapPenalty * (j - 2));
                        runningIndex = j - 2;
                    }
                }

                if (j < i || char.ToLowerInvariant(candidate[j]) != pc)
                {
                    scores[cell] = double.NegativeInfinity;
                    previous[cell] = -1;
                    continue;
                }

                var own = MatchScore + Bonus(candidate, j);
                if (i == 0)
                {
                    scores[cell] = own - Math.Min(MaxLeadingGapPenalty, j * LeadingGapPenalty);
                    previous[cell] = -1;
                    continue;
                }

                var consecutive = j >= 1 ? scores[((i - 1) * m) + j - 1] : double.NegativeInfinity;
                if (!double.IsNegativeInfinity(consecutive))
                {
                    consecutive += ConsecutiveBonus;
                }

                // Gap of (j - k - 1) characters after a match at k.
                var gapped = double.IsNegativeInfinity(runningBest)
                    ? double.NegativeInfinity
                    : runningBest - GapStartPenalty - (GapPenalty * (j - 1));

                if (double.IsNegativeInfinity(consecutive) && double.IsNegativeInfinity(gapped))
                {
                    scores[cell] = double.NegativeInfinity;
                    previous[cell] = -1;
                }
                else if (consecutive >= gapped)
                {
                    scores[cell] = own + consecutive;
                    previous[cell] = j - 1;
                }
                else
                {
                    scores[cell] = own + gapped;
                    previous[cell] = runningIndex;
                }
            }
        }
    }

    private static double Bonus(string text, int index)
    {
        if (index == 0)
        {
            return FirstCharacterBonus;
        }

        var current = text[index];
        var before = text[index - 1];
        if (IsSeparator(before))
        {
            return WordStartBonus;
        }

        if ((char.IsUpper(current) && char.IsLower(before)) || (char.IsDigit(current) && char.IsLetter(before)))
        {
            return CamelCaseBonus;
        }

        return 0;
    }

    private static bool IsSeparator(char c) =>
        char.IsWhiteSpace(c) || c is '-' or '_' or '/' or '\\' or '.' or ':' or '@' or '#' or '(' or ')' or '[' or ']' or ',' or '+';

    private static bool IsSubsequence(string pattern, string candidate)
    {
        var i = 0;
        for (var j = 0; j < candidate.Length && i < pattern.Length; j++)
        {
            if (char.ToLowerInvariant(candidate[j]) == char.ToLowerInvariant(pattern[i]))
            {
                i++;
            }
        }

        return i == pattern.Length;
    }
}
