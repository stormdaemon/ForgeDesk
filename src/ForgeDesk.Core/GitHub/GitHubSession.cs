using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// In-memory state of the signed-in GitHub session (token + account). The account service
/// is the only writer; the API client, the service cache and the git credential provider read
/// it. Keeping it separate from <see cref="GitHubAccountService"/> avoids a dependency cycle
/// (git service → credential provider → account service → git service).
/// </summary>
internal sealed class GitHubSession
{
    private volatile Snapshot? _current;

    public event EventHandler<EventArgs>? Changed;

    /// <summary>The token of the current session, or null when signed out. Never log it.</summary>
    public string? Token => _current?.Token;

    public GitHubAccount? Account => _current?.Account;

    public void Set(string token, GitHubAccount account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(account);
        _current = new Snapshot(token, account);
        SafeEvent.Raise(Changed, this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_current is null)
        {
            return;
        }

        _current = null;
        SafeEvent.Raise(Changed, this, EventArgs.Empty);
    }

    // A class rather than a record so the token never ends up in a generated ToString().
    private sealed class Snapshot(string token, GitHubAccount account)
    {
        public string Token { get; } = token;

        public GitHubAccount Account { get; } = account;
    }
}
