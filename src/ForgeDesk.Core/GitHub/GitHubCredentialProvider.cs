using ForgeDesk.Core.Git;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Lets git push/pull/fetch over HTTPS to github.com with the signed-in account's token, so a
/// user signed in to ForgeDesk isn't asked to sign in again by git. SSH remotes, other hosts and
/// URLs carrying their own credentials are left to git's configured helpers.
/// </summary>
internal sealed class GitHubCredentialProvider : IGitCredentialProvider
{
    /// <summary>GitHub accepts any user name with a token; this one is what GitHub's own tools use.</summary>
    public const string TokenUserName = "x-access-token";

    private readonly GitHubSession _session;
    private readonly ISettingsService _settings;

    public GitHubCredentialProvider(GitHubSession session, ISettingsService settings)
    {
        _session = session;
        _settings = settings;
    }

    public Task<GitCredential?> GetCredentialAsync(string remoteUrl, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.UseGitHubTokenForGit || !settings.GitHubEnabled || !IsGitHubHttpsRemote(remoteUrl))
        {
            return Task.FromResult<GitCredential?>(null);
        }

        var token = _session.Token;
        return Task.FromResult(string.IsNullOrEmpty(token) ? null : new GitCredential(TokenUserName, token));
    }

    internal static bool IsGitHubHttpsRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Never send the token over plain HTTP, to another port, or over a URL with its own password.
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && !uri.UserInfo.Contains(':', StringComparison.Ordinal)
            && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase));
    }
}
