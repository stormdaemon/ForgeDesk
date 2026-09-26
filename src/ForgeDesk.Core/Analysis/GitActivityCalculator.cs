using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Analysis;

/// <summary>Commit statistics from a (possibly truncated) newest-first git log.</summary>
internal static class GitActivityCalculator
{
    public const int Weeks = 12;

    public static GitActivity Compute(IReadOnlyList<GitCommit> commits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(commits);

        // Rolling 7-day windows ending now; the last bucket is the current week.
        var weekly = new int[Weeks];
        var last30 = 0;
        var last90 = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (var commit in commits)
        {
            var when = commit.Author.When;
            first = first is null || when < first ? when : first;
            last = last is null || when > last ? when : last;

            var age = now - when;
            if (age < TimeSpan.Zero)
            {
                // Clock skew between machines: count future-dated commits as "now".
                age = TimeSpan.Zero;
            }

            if (age <= TimeSpan.FromDays(30))
            {
                last30++;
            }

            if (age <= TimeSpan.FromDays(90))
            {
                last90++;
            }

            var week = (int)(age.TotalDays / 7);
            if (week < Weeks)
            {
                weekly[Weeks - 1 - week]++;
            }
        }

        return new GitActivity
        {
            CommitsLast30Days = last30,
            CommitsLast90Days = last90,
            TotalCommits = commits.Count,
            FirstCommitAt = first,
            LastCommitAt = last,
            WeeklyCommits = weekly,
            TopContributors = TopContributors(commits, AnalysisLimits.TopContributors),
        };
    }

    /// <summary>
    /// Groups identities that share a name or an e-mail (case-insensitively), so "Ana" committing
    /// from work and personal addresses counts once; the most used spelling of the name is shown.
    /// </summary>
    public static IReadOnlyList<ContributorStat> TopContributors(IReadOnlyList<GitCommit> commits, int count)
    {
        var groups = new DisjointSets();
        foreach (var commit in commits)
        {
            groups.Union(NameKey(commit.Author), EmailKey(commit.Author));
        }

        return commits
            .GroupBy(c => groups.Find(NameKey(c.Author)))
            .Select(g => new ContributorStat(
                g.GroupBy(c => c.Author.Name.Trim(), StringComparer.Ordinal)
                    .OrderByDescending(n => n.Count())
                    .ThenBy(n => n.Key, StringComparer.Ordinal)
                    .First().Key is { Length: > 0 } name ? name : g.First().Author.Email,
                g.Count()))
            .OrderByDescending(c => c.Commits)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }

    private static string NameKey(GitSignature signature)
    {
        var name = signature.Name.Trim();
        if (name.Length > 0)
        {
            return "n:" + name.ToUpperInvariant();
        }

        var email = signature.Email.Trim();
        return email.Length > 0 ? "e:" + email.ToUpperInvariant() : "?";
    }

    private static string EmailKey(GitSignature signature)
    {
        var email = signature.Email.Trim();
        return email.Length > 0 ? "e:" + email.ToUpperInvariant() : NameKey(signature);
    }

    private sealed class DisjointSets
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

        public string Find(string key)
        {
            var root = key;
            while (_parent.TryGetValue(root, out var parent) && parent != root)
            {
                root = parent;
            }

            // Path compression keeps later lookups flat.
            while (key != root && _parent.TryGetValue(key, out var next))
            {
                _parent[key] = root;
                key = next;
            }

            _parent.TryAdd(root, root);
            return root;
        }

        public void Union(string a, string b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
            {
                _parent[rootB] = rootA;
            }
        }
    }
}
