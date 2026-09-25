using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Security;
using ForgeDesk.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Signs in to GitHub (token, Git Credential Manager, GitHub CLI), persists the session and
/// restores it at startup. The token lives in the OS vault (<see cref="ISecretStore"/>); the
/// login is also kept in settings so the UI can show it before the token is re-validated.
/// </summary>
internal sealed class GitHubAccountService : IGitHubAccountService
{
    internal const string TokenSecretKey = "ForgeDesk:github.com";

    /// <summary>Non-secret session details (method, profile, scopes) stored next to the token.</summary>
    internal const string SessionSecretKey = "ForgeDesk:github.com:session";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly GitHubSession _session;
    private readonly ISecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly GitHubTokenValidator _validator;
    private readonly GitCredentialManagerClient _credentialManager;
    private readonly GitHubCliClient _cli;
    private readonly ILogger<GitHubAccountService> _logger;

    // Serializes state transitions (restore, commit of a sign-in, sign-out). Sign-in
    // prompts and validation run outside it so a pending browser sign-in never blocks sign-out.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubAccountService(
        GitHubSession session,
        ISecretStore secrets,
        ISettingsService settings,
        GitHubTokenValidator validator,
        GitCredentialManagerClient credentialManager,
        GitHubCliClient cli,
        ILogger<GitHubAccountService>? logger = null)
    {
        _session = session;
        _secrets = secrets;
        _settings = settings;
        _validator = validator;
        _credentialManager = credentialManager;
        _cli = cli;
        _logger = logger ?? NullLogger<GitHubAccountService>.Instance;
    }

    public event EventHandler<GitHubAccount?>? AccountChanged;

    public GitHubAccount? Current => _session.Account;

    public async Task<GitHubAccount?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        GitHubAccount? account;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            account = await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        SafeEvent.Raise(AccountChanged, this, account);
        return account;
    }

    public async Task<GitHubAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var normalized = GitHubTokens.Normalize(token);
        var validated = await ValidateForSignInAsync(normalized,
            "GitHub rejected this token.",
            "Check that you copied the whole token and that it hasn't expired or been revoked. You can create a new one at https://github.com/settings/tokens.",
            onRejected: null,
            cancellationToken).ConfigureAwait(false);
        return await CommitAsync(normalized, validated, GitHubAuthMethod.PersonalAccessToken).ConfigureAwait(false);
    }

    public async Task<GitHubAccount> SignInWithGitCredentialManagerAsync(CancellationToken cancellationToken = default)
    {
        var credential = await _credentialManager.FillAsync(cancellationToken).ConfigureAwait(false);
        var token = credential.Password!;
        var validated = await ValidateForSignInAsync(token,
            "The GitHub sign-in saved in Git Credential Manager is no longer valid.",
            "Try again: Git Credential Manager will open the GitHub sign-in in your browser.",
            // Make GCM forget the stale credential so the next attempt opens the browser sign-in.
            onRejected: () => _credentialManager.RejectAsync(credential, CancellationToken.None),
            cancellationToken).ConfigureAwait(false);

        await _credentialManager.ApproveAsync(credential, CancellationToken.None).ConfigureAwait(false);
        return await CommitAsync(token, validated, GitHubAuthMethod.GitCredentialManager).ConfigureAwait(false);
    }

    public async Task<GitHubAccount> SignInWithGitHubCliAsync(CancellationToken cancellationToken = default)
    {
        var token = await _cli.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var validated = await ValidateForSignInAsync(token,
            "The GitHub CLI's sign-in is no longer valid.",
            "Run 'gh auth login' in a terminal, then try again.",
            onRejected: null,
            cancellationToken).ConfigureAwait(false);
        return await CommitAsync(token, validated, GitHubAuthMethod.GitHubCli).ConfigureAwait(false);
    }

    public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) =>
        _cli.IsAvailableAsync(cancellationToken);

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ForgetAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Signed out of GitHub");
        SafeEvent.Raise(AccountChanged, this, null);
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(_session.Token);

    private async Task<GitHubAccount?> RestoreCoreAsync(CancellationToken cancellationToken)
    {
        var token = await _secrets.GetAsync(TokenSecretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            await ForgetAsync().ConfigureAwait(false);
            return null;
        }

        var stored = await ReadStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        ValidatedToken validated;
        try
        {
            validated = await _validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnauthorized(ex))
        {
            _logger.LogWarning("The saved GitHub token was rejected (401); signing out");
            await ForgetAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (GitHubErrorTranslator.TryTranslate(ex, null, cancellationToken, out _))
        {
            // Offline, GitHub outage, rate limit…: stay signed in with what we know.
            _logger.LogWarning("Could not verify the GitHub session ({Reason}); continuing unverified", ex.Message);
            var offline = BuildUnverifiedAccount(token, stored);
            if (offline is null)
            {
                _session.Clear();
                return null;
            }

            _session.Set(token, offline);
            return offline;
        }

        var account = CreateAccount(token, validated, stored?.Method ?? GitHubAuthMethod.PersonalAccessToken);
        await PersistAsync(token: null, account).ConfigureAwait(false);
        _session.Set(token, account);
        _logger.LogInformation("Restored GitHub session for {Login}", account.User.Login);
        return account;
    }

    private async Task<ValidatedToken> ValidateForSignInAsync(string token, string rejectedMessage, string rejectedHint, Func<Task>? onRejected, CancellationToken cancellationToken)
    {
        try
        {
            return await _validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnauthorized(ex))
        {
            if (onRejected is not null)
            {
                await onRejected().ConfigureAwait(false);
            }

            throw new ForgeException(ErrorKind.AuthenticationFailed, rejectedMessage, rejectedHint, null, ex);
        }
        catch (Exception ex) when (GitHubErrorTranslator.TryTranslate(ex, "Your GitHub account", cancellationToken, out var translated) && !ReferenceEquals(ex, translated))
        {
            throw translated;
        }
    }

    private async Task<GitHubAccount> CommitAsync(string token, ValidatedToken validated, GitHubAuthMethod method)
    {
        var account = CreateAccount(token, validated, method);

        // Once GitHub accepted the token the sign-in must be recorded completely: not cancellable.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await PersistAsync(token, account).ConfigureAwait(false);
            _session.Set(token, account);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Signed in to GitHub as {Login} ({Method})", account.User.Login, method);
        SafeEvent.Raise(AccountChanged, this, account);
        return account;
    }

    private static GitHubAccount CreateAccount(string token, ValidatedToken validated, GitHubAuthMethod method) =>
        new(validated.User, validated.Scopes, method)
        {
            Warning = GitHubTokens.DescribeMissingScopes(token, validated.Scopes),
        };

    private GitHubAccount? BuildUnverifiedAccount(string token, StoredSession? stored)
    {
        var login = stored?.Login ?? _settings.Current.GitHubLogin;
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        var user = new GitHubUser(
            login,
            stored?.Name,
            stored?.AvatarUrl ?? $"https://github.com/{login}.png",
            stored?.HtmlUrl ?? $"https://github.com/{login}");
        var scopes = stored?.Scopes ?? [];
        return new GitHubAccount(user, scopes, stored?.Method ?? GitHubAuthMethod.PersonalAccessToken)
        {
            IsVerified = false,
            Warning = GitHubTokens.DescribeMissingScopes(token, scopes),
        };
    }

    /// <param name="token">The token to store, or null to keep the stored one.</param>
    private async Task PersistAsync(string? token, GitHubAccount account)
    {
        if (token is not null)
        {
            await _secrets.SetAsync(TokenSecretKey, token, CancellationToken.None).ConfigureAwait(false);
        }

        var stored = new StoredSession(account.Method, account.User.Login, account.User.Name, account.User.AvatarUrl, account.User.HtmlUrl, account.Scopes);
        await _secrets.SetAsync(SessionSecretKey, JsonSerializer.Serialize(stored, JsonOptions), CancellationToken.None).ConfigureAwait(false);

        if (!string.Equals(_settings.Current.GitHubLogin, account.User.Login, StringComparison.Ordinal))
        {
            await _settings.UpdateAsync(s => s with { GitHubLogin = account.User.Login }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ForgetAsync()
    {
        _session.Clear();
        await DeleteSecretIfPresentAsync(TokenSecretKey).ConfigureAwait(false);
        await DeleteSecretIfPresentAsync(SessionSecretKey).ConfigureAwait(false);
        if (_settings.Current.GitHubLogin is not null)
        {
            await _settings.UpdateAsync(s => s with { GitHubLogin = null }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Vault implementations may treat deleting a missing entry as an error.
    private async Task DeleteSecretIfPresentAsync(string key)
    {
        if (await _secrets.GetAsync(key, CancellationToken.None).ConfigureAwait(false) is not null)
        {
            await _secrets.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<StoredSession?> ReadStoredSessionAsync(CancellationToken cancellationToken)
    {
        var json = await _secrets.GetAsync(SessionSecretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(stored?.Login) ? null : stored;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable GitHub session details");
            return null;
        }
    }

    private static bool IsUnauthorized(Exception exception) =>
        exception is ApiException { StatusCode: HttpStatusCode.Unauthorized };

    private sealed record StoredSession(
        GitHubAuthMethod Method,
        string Login,
        string? Name,
        string? AvatarUrl,
        string? HtmlUrl,
        IReadOnlyList<string>? Scopes);
}
