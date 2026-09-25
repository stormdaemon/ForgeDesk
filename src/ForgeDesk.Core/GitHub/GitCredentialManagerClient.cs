using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Talks to the credential helper configured in git (Git Credential Manager on Windows) with
/// <c>git credential fill/approve/reject</c>. GCM shows its own browser sign-in window when it
/// has no stored github.com credential.
/// </summary>
internal sealed class GitCredentialManagerClient
{
    internal static readonly TimeSpan FillTimeout = TimeSpan.FromMinutes(5);
    internal const string FillRequest = "protocol=https\nhost=github.com\n\n";

    private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(30);
    private const string InstallHint = "Install Git for Windows from https://git-scm.com/download/win (it includes Git Credential Manager), or sign in with a personal access token instead.";

    private readonly IGitService _git;
    private readonly IProcessRunner _runner;
    private readonly ILogger<GitCredentialManagerClient> _logger;

    public GitCredentialManagerClient(IGitService git, IProcessRunner runner, ILogger<GitCredentialManagerClient>? logger = null)
    {
        _git = git;
        _runner = runner;
        _logger = logger ?? NullLogger<GitCredentialManagerClient>.Instance;
    }

    /// <summary>Asks the credential helper for github.com credentials, waiting up to 5 minutes for the browser sign-in.</summary>
    public async Task<GitCredentialFill> FillAsync(CancellationToken cancellationToken)
    {
        var git = await FindGitAsync(cancellationToken).ConfigureAwait(false);
        var result = await _runner.RunAsync(CreateSpec(git, "fill", FillRequest, FillTimeout), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new ForgeException(ErrorKind.Timeout, "Signing in with Git Credential Manager took too long.",
                "Finish the sign-in in the browser window within 5 minutes, then try again.");
        }

        var credential = GitCredentialFill.Parse(result.StandardOutput);
        if (result.ExitCode == 0 && !string.IsNullOrEmpty(credential.Password))
        {
            return credential;
        }

        throw DescribeFailure(result);
    }

    /// <summary>Tells the helper the credential worked so it keeps it. Best effort.</summary>
    public Task ApproveAsync(GitCredentialFill credential, CancellationToken cancellationToken) =>
        StoreAsync("approve", credential, cancellationToken);

    /// <summary>Tells the helper the credential was rejected so it asks again next time. Best effort.</summary>
    public Task RejectAsync(GitCredentialFill credential, CancellationToken cancellationToken) =>
        StoreAsync("reject", credential, cancellationToken);

    private async Task StoreAsync(string action, GitCredentialFill credential, CancellationToken cancellationToken)
    {
        try
        {
            var git = await FindGitAsync(cancellationToken).ConfigureAwait(false);
            var result = await _runner.RunAsync(CreateSpec(git, action, credential.ToProtocolInput(), StoreTimeout), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("git credential {Action} failed (exit code {ExitCode}): {Error}", action, result.ExitCode, result.StandardError.Trim());
            }
        }
        catch (ForgeException ex)
        {
            _logger.LogWarning(ex, "git credential {Action} could not run", action);
        }
    }

    private async Task<string> FindGitAsync(CancellationToken cancellationToken)
    {
        var installation = await _git.FindGitAsync(cancellationToken).ConfigureAwait(false);
        return installation?.ExecutablePath
            ?? throw new ForgeException(ErrorKind.GitNotFound, "Git isn't installed, so Git Credential Manager can't be used.", InstallHint);
    }

    private static ProcessSpec CreateSpec(string git, string action, string input, TimeSpan timeout) => new()
    {
        FileName = git,
        Arguments = ["credential", action],
        StandardInput = input,
        Timeout = timeout,
        MaxCapturedChars = 64 * 1024,
        Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Git must never wait for terminal input nobody can see; GCM uses its own window.
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
        },
    };

    private static ForgeException DescribeFailure(ProcessResult result)
    {
        var error = result.StandardError.Trim();
        if (ContainsAny(error, "terminal prompts disabled", "could not read username", "could not read password", "is not a git command", "credential-manager: not found"))
        {
            return new ForgeException(ErrorKind.ToolNotFound, "Git Credential Manager isn't set up on this computer.", InstallHint, error);
        }

        if (ContainsAny(error, "cancel"))
        {
            return new ForgeException(ErrorKind.Cancelled, "Sign-in was cancelled.", null, error);
        }

        return new ForgeException(ErrorKind.ProcessFailed, "Git Credential Manager couldn't sign you in to GitHub.",
            "Try again, or sign in with a personal access token instead.",
            string.IsNullOrEmpty(error) ? $"git credential fill exited with code {result.ExitCode}." : error);
    }

    private static bool ContainsAny(string text, params string[] fragments) =>
        fragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Attributes returned by <c>git credential fill</c>, in order (keys such as "wwwauth[]" may
/// repeat). A class rather than a record: a generated ToString() would print the password.
/// </summary>
internal sealed class GitCredentialFill
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _fields;

    public GitCredentialFill(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        _fields = fields;
    }

    public string? Username => Get("username");

    public string? Password => Get("password");

    public static GitCredentialFill Parse(string output)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            fields.Add(new KeyValuePair<string, string>(line[..separator], line[(separator + 1)..]));
        }

        return new GitCredentialFill(fields);
    }

    /// <summary>The same attributes in git's credential protocol, for approve/reject.</summary>
    public string ToProtocolInput()
    {
        var builder = new System.Text.StringBuilder();
        if (Get("protocol") is null)
        {
            builder.Append("protocol=https\n");
        }

        if (Get("host") is null)
        {
            builder.Append("host=github.com\n");
        }

        foreach (var (key, value) in _fields)
        {
            // "quit" only makes sense as input to fill.
            if (!string.Equals(key, "quit", StringComparison.Ordinal))
            {
                builder.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return builder.Append('\n').ToString();
    }

    public override string ToString() => $"git credential for {Get("host") ?? "github.com"} (user {Username ?? "unknown"})";

    private string? Get(string key)
    {
        foreach (var (k, v) in _fields)
        {
            if (string.Equals(k, key, StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }
}
