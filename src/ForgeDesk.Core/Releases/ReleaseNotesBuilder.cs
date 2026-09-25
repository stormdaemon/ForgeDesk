using System.Text;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Releases;

/// <summary>
/// Drafts Markdown release notes from commits, grouped by Conventional Commit type.
/// Public so the release wizard can rebuild the draft when the user picks another version.
/// </summary>
public static class ReleaseNotesBuilder
{
    private static readonly (ChangeCategory Category, string Heading)[] Sections =
    [
        (ChangeCategory.Breaking, "⚠️ Breaking changes"),
        (ChangeCategory.Feature, "✨ Features"),
        (ChangeCategory.Fix, "🐛 Fixes"),
        (ChangeCategory.Performance, "⚡ Performance"),
        (ChangeCategory.Documentation, "📝 Documentation"),
        (ChangeCategory.Maintenance, "🔧 Maintenance"),
        (ChangeCategory.Other, "Other changes"),
    ];

    /// <param name="commits">Commits of the release, in git log order (newest first).</param>
    /// <param name="repository">GitHub repository, for the "Full changelog" link.</param>
    /// <param name="previousTag">Tag of the previous release, if any.</param>
    /// <param name="newTag">Tag of the release being prepared.</param>
    public static string Build(IReadOnlyList<GitCommit> commits, GitHubRepoRef? repository = null, string? previousTag = null, string? newTag = null)
    {
        ArgumentNullException.ThrowIfNull(commits);

        var entries = commits
            .Where(c => !ConventionalCommit.IsMerge(c))
            .Select(c => (Commit: c, Parsed: ConventionalCommit.Parse(c)))
            .Where(e => e.Parsed.Description.Length > 0)
            .ToList();

        var notes = new StringBuilder();
        if (entries.Count == 0)
        {
            notes.Append(previousTag is null ? "First release." : $"No changes since {previousTag}.").Append('\n');
        }

        foreach (var (category, heading) in Sections)
        {
            var lines = entries.Where(e => e.Parsed.Category == category).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            if (notes.Length > 0)
            {
                notes.Append('\n');
            }

            notes.Append("### ").Append(heading).Append('\n');
            foreach (var (commit, parsed) in lines)
            {
                notes.Append("- ").Append(FormatLine(parsed)).Append(" (").Append(commit.ShortSha).Append(")\n");
            }
        }

        if (repository is not null && !string.IsNullOrWhiteSpace(previousTag) && !string.IsNullOrWhiteSpace(newTag))
        {
            notes.Append('\n')
                .Append("**Full changelog**: ")
                .Append(repository.HtmlUrl).Append("/compare/")
                .Append(Uri.EscapeDataString(previousTag)).Append("...").Append(Uri.EscapeDataString(newTag))
                .Append('\n');
        }

        return notes.ToString();
    }

    private static string FormatLine(ConventionalCommit commit)
    {
        var description = Capitalize(commit.Description);
        return commit.Scope is null ? description : $"**{commit.Scope}:** {description}";
    }

    private static string Capitalize(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
}
