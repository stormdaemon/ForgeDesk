using Octokit;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Hands out the Octokit client of the signed-in account. The client is rebuilt whenever the
/// session token changes (sign-in, sign-out, account switch).
/// </summary>
internal sealed class GitHubClientProvider
{
    private readonly GitHubClientFactory _factory;
    private readonly GitHubSession _session;
    private CachedClient? _cached;

    public GitHubClientProvider(GitHubClientFactory factory, GitHubSession session)
    {
        _factory = factory;
        _session = session;
    }

    /// <summary>The client for the current token; throws AuthenticationRequired when signed out.</summary>
    public GitHubClient GetClient() => GetClientAndToken().Client;

    /// <summary>The client plus the token it was built with (needed for requests Octokit can't make).</summary>
    public (GitHubClient Client, string Token) GetClientAndToken()
    {
        var token = _session.Token ?? throw GitHubErrorTranslator.NotSignedIn();
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && string.Equals(cached.Token, token, StringComparison.Ordinal))
        {
            return (cached.Client, token);
        }

        var created = new CachedClient(token, _factory.CreateClient(token));
        Volatile.Write(ref _cached, created);
        return (created.Client, token);
    }

    /// <summary>Rate-limit and scope headers of the last response received by the current client.</summary>
    public ApiInfo? GetLastApiInfo()
    {
        var cached = Volatile.Read(ref _cached);
        return cached is not null && string.Equals(cached.Token, _session.Token, StringComparison.Ordinal)
            ? cached.Client.GetLastApiInfo()
            : null;
    }

    private sealed class CachedClient(string token, GitHubClient client)
    {
        public string Token { get; } = token;

        public GitHubClient Client { get; } = client;
    }
}
