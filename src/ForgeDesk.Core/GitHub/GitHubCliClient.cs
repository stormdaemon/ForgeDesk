using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;

namespace ForgeDesk.Core.GitHub;

internal interface IGitHubCliLocator
{
    /// <summary>Full path of the GitHub CLI executable, or null when it isn't installed.</summary>
    string? Find();
}

/// <summary>Finds gh on PATH, then in the folders its Windows installers use.</summary>
internal sealed class GitHubCliLocator : IGitHubCliLocator
{
    public string? Find() =>
        ExecutableLocator.Find("gh") ?? KnownLocations().FirstOrDefault(File.Exists);

    private static IEnumerable<string> KnownLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        // MSI / winget install, per-user install, Scoop shim. PATH may lack them in a process
        // started before the installation.
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var folder = Environment.GetFolderPath(root);
            if (!string.IsNullOrEmpty(folder))
            {
                yield return Path.Combine(folder, "GitHub CLI", "gh.exe");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "scoop", "shims", "gh.exe");
        }
    }
}

/// <summary>Reads the token of an authenticated GitHub CLI (<c>gh auth token</c>).</summary>
internal sealed class GitHubCliClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string InstallHint = "Install it with 'winget install GitHub.cli' or from https://cli.github.com, run 'gh auth login', then try again.";

    private readonly IGitHubCliLocator _locator;
    private readonly IProcessRunner _runner;

    public GitHubCliClient(IGitHubCliLocator locator, IProcessRunner runner)
    {
        _locator = locator;
        _runner = runner;
    }

    // Probing PATH × PATHEXT is dozens of file-system checks: keep them off the UI thread.
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _locator.Find() is not null, cancellationToken);

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var gh = await Task.Run(_locator.Find, cancellationToken).ConfigureAwait(false)
            ?? throw new ForgeException(ErrorKind.ToolNotFound, "The GitHub CLI isn't installed.", InstallHint);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(new ProcessSpec
            {
                FileName = gh,
                Arguments = ["auth", "token", "--hostname", "github.com"],
                Timeout = Timeout,
                MaxCapturedChars = 16 * 1024,
                Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GH_PROMPT_DISABLED"] = "1",
                    ["GH_NO_UPDATE_NOTIFIER"] = "1",
                    ["NO_COLOR"] = "1",
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.ToolNotFound)
        {
            throw new ForgeException(ErrorKind.ToolNotFound, "The GitHub CLI couldn't be started.", InstallHint, ex.Detail, ex);
        }

        if (result.TimedOut)
        {
            throw new ForgeException(ErrorKind.Timeout, "The GitHub CLI didn't answer in time.", "Run 'gh auth status' in a terminal to check it, then try again.");
        }

        var token = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (result.ExitCode == 0 && !string.IsNullOrEmpty(token) && !token.Contains(' ', StringComparison.Ordinal))
        {
            return token;
        }

        // stderr never contains the token, so it is safe to show as detail.
        var error = result.StandardError.Trim();
        if (result.ExitCode != 0 && (error.Contains("not logged", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no oauth token", StringComparison.OrdinalIgnoreCase)
            || error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ForgeException(ErrorKind.AuthenticationRequired, "The GitHub CLI isn't signed in to github.com.",
                "Run 'gh auth login' in a terminal, then try again.", error);
        }

        throw new ForgeException(ErrorKind.ProcessFailed, "The GitHub CLI couldn't provide a token.",
            "Run 'gh auth status' in a terminal to check the CLI's sign-in.",
            string.IsNullOrEmpty(error) ? $"gh auth token exited with code {result.ExitCode}." : error);
    }
}
