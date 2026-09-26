using System.Text.RegularExpressions;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Releases;

/// <summary>Release-notes category of a commit.</summary>
internal enum ChangeCategory
{
    Breaking,
    Feature,
    Fix,
    Performance,
    Documentation,
    Maintenance,
    Other,
}

/// <summary>A commit subject read with the Conventional Commits convention ("feat(ui)!: add dark mode").</summary>
internal sealed partial record ConventionalCommit(string? Type, string? Scope, string Description, bool IsBreaking)
{
    private static readonly Dictionary<string, ChangeCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["feat"] = ChangeCategory.Feature,
        ["feature"] = ChangeCategory.Feature,
        ["fix"] = ChangeCategory.Fix,
        ["perf"] = ChangeCategory.Performance,
        ["docs"] = ChangeCategory.Documentation,
        ["doc"] = ChangeCategory.Documentation,
        ["refactor"] = ChangeCategory.Maintenance,
        ["chore"] = ChangeCategory.Maintenance,
        ["build"] = ChangeCategory.Maintenance,
        ["ci"] = ChangeCategory.Maintenance,
        ["test"] = ChangeCategory.Maintenance,
        ["tests"] = ChangeCategory.Maintenance,
        ["style"] = ChangeCategory.Maintenance,
    };

    /// <summary>The section the commit belongs to; breaking changes always go first.</summary>
    public ChangeCategory Category =>
        IsBreaking ? ChangeCategory.Breaking
        : Type is not null && Categories.TryGetValue(Type, out var category) ? category
        : ChangeCategory.Other;

    public static ConventionalCommit Parse(string subject, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var trimmed = subject.Trim();
        var breakingFooter = body is not null && BreakingFooter().IsMatch(body);
        var match = Header().Match(trimmed);

        // Only known types are stripped: "README: fix typo" is a plain subject, not a commit of type "README".
        if (!match.Success || !Categories.ContainsKey(match.Groups["type"].Value))
        {
            return new ConventionalCommit(null, null, trimmed, breakingFooter);
        }

        var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value.Trim() : null;
        return new ConventionalCommit(
            match.Groups["type"].Value.ToLowerInvariant(),
            string.IsNullOrEmpty(scope) ? null : scope,
            match.Groups["description"].Value.Trim(),
            match.Groups["bang"].Success || breakingFooter);
    }

    public static ConventionalCommit Parse(GitCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return Parse(commit.Subject, commit.Body);
    }

    /// <summary>Merge commits add no information of their own to release notes.</summary>
    public static bool IsMerge(GitCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return commit.IsMerge
            || commit.Subject.StartsWith("Merge pull request ", StringComparison.Ordinal)
            || commit.Subject.StartsWith("Merge branch ", StringComparison.Ordinal)
            || commit.Subject.StartsWith("Merge remote-tracking branch ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z]+)(?:\((?<scope>[^()\r\n]*)\))?(?<bang>!)?:\s*(?<description>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"^BREAKING[ -]CHANGE:", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BreakingFooter();
}
