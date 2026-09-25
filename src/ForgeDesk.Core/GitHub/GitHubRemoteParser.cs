using System.Text.RegularExpressions;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.GitHub;

/// <summary>Extracts owner/repo from any GitHub remote URL form (https, ssh, scp-like).</summary>
public static partial class GitHubRemoteParser
{
    public static GitHubRepoRef? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var match = RemotePattern().Match(remoteUrl.Trim());
        if (!match.Success)
        {
            return null;
        }

        var owner = match.Groups["owner"].Value;
        var name = match.Groups["repo"].Value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return owner.Length == 0 || name.Length == 0 ? null : new GitHubRepoRef(owner, name);
    }

    public static bool IsGitHubUrl(string? remoteUrl) => Parse(remoteUrl) is not null;

    // https://github.com/o/r(.git), http://…, https://user@github.com/o/r, git://github.com/o/r,
    // ssh://git@github.com/o/r, git@github.com:o/r, ssh.github.com:443 variants.
    [GeneratedRegex(@"^(?:(?:https?|git|ssh)://)?(?:[^@/\s]+@)?(?:ssh\.)?github\.com(?::\d+)?[:/](?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]+?)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemotePattern();
}
