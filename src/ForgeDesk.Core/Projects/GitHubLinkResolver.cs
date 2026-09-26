using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Projects;

/// <summary>Finds the GitHub repository a project folder is linked to, from its git remotes.</summary>
internal static class GitHubLinkResolver
{
    public const string PreferredRemote = "origin";

    /// <summary>
    /// The GitHub repository of "origin", else of the first remote pointing to GitHub. The link is
    /// optional metadata, so git being absent, the folder not being a repository or git failing
    /// never prevents registering the project.
    /// </summary>
    public static async Task<GitHubRepoRef?> ResolveAsync(IGitService git, string path, ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitRemote> remotes;
        try
        {
            remotes = await git.GetRemotesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.Kind is ErrorKind.NotARepository or ErrorKind.GitNotFound)
        {
            return null;
        }
        catch (ForgeException ex)
        {
            logger.LogWarning(ex, "Could not read the git remotes of {Path}", path);
            return null;
        }

        return Pick(remotes);
    }

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
