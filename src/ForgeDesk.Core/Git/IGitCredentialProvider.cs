namespace ForgeDesk.Core.Git;

/// <summary>HTTP credentials to inject into a git network operation.</summary>
public sealed record GitCredential(string Username, string Password);

/// <summary>
/// Supplies credentials for git remotes (the GitHub domain provides the signed-in token for
/// github.com remotes). The git service passes them through environment-based configuration
/// (GIT_CONFIG_COUNT/KEY/VALUE), never on the command line. Return null to let git's own
/// credential helpers (Git Credential Manager) handle authentication.
/// </summary>
public interface IGitCredentialProvider
{
    Task<GitCredential?> GetCredentialAsync(string remoteUrl, CancellationToken cancellationToken = default);
}
