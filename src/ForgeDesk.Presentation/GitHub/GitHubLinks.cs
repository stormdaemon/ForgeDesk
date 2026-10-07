using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>How ForgeDesk links a project to its GitHub repository: through its git remotes.</summary>
public static class GitHubLinks
{
    public const string PreferredRemote = "origin";

    /// <summary>The GitHub repository of "origin", else of the first remote pointing to GitHub; null when none does.</summary>
    public static GitHubRepoRef? Pick(IReadOnlyList<GitRemote> remotes)
    {
        ArgumentNullException.ThrowIfNull(remotes);
        var origin = remotes.FirstOrDefault(r => string.Equals(r.Name, PreferredRemote, StringComparison.Ordinal));
        if (origin is not null && Parse(origin) is { } fromOrigin)
        {
            return fromOrigin;
        }

        return remotes.Select(Parse).FirstOrDefault(r => r is not null);
    }

    private static GitHubRepoRef? Parse(GitRemote remote) =>
        GitHubRemoteParser.Parse(remote.FetchUrl) ?? GitHubRemoteParser.Parse(remote.PushUrl);
}
