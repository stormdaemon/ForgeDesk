namespace ForgeDesk.Core.GitHub;

internal sealed record ValidatedToken(GitHubUser User, IReadOnlyList<string> Scopes);

/// <summary>Validates a candidate token with <c>GET /user</c> and reads its OAuth scopes.</summary>
internal sealed class GitHubTokenValidator
{
    private readonly GitHubClientFactory _factory;

    public GitHubTokenValidator(GitHubClientFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Throws Octokit/transport exceptions untranslated so callers can react to 401 specifically.</summary>
    public async Task<ValidatedToken> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(token);
        var user = await client.User.Current().WaitOrAbandonAsync(cancellationToken).ConfigureAwait(false);
        var scopes = client.GetLastApiInfo()?.OauthScopes ?? [];
        return new ValidatedToken(GitHubMapper.ToUser(user), scopes.ToList());
    }
}
