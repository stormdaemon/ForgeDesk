using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Git;

/// <summary>
/// <see cref="IGitService"/> on top of the git command line. Every command goes through
/// <see cref="GitCli"/> (argument lists, fixed environment, error translation) and reads only
/// machine-readable output. Split by area across partial files.
/// </summary>
internal sealed partial class GitService : IGitService
{
    /// <summary>Settings git needs to print log records exactly as <see cref="GitLogParser"/> expects.</summary>
    private static readonly string[] LogConfig = ["log.showSignature=false", "i18n.logOutputEncoding=UTF-8"];

    private readonly GitCli _cli;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<IGitCredentialProvider> _credentialProviders;
    private readonly ILogger _logger;

    public GitService(ISettingsService settings, IProcessRunner runner, IEnumerable<IGitCredentialProvider> credentialProviders, ILogger<GitService>? logger = null)
        : this(settings, runner, credentialProviders, new Dictionary<string, string?>(), logger)
    {
    }

    /// <param name="baseEnvironment">Variables for every git process; tests point GIT_CONFIG_GLOBAL at a private file.</param>
    internal GitService(ISettingsService settings, IProcessRunner runner, IEnumerable<IGitCredentialProvider> credentialProviders,
        IReadOnlyDictionary<string, string?> baseEnvironment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(credentialProviders);
        _settings = settings;
        _credentialProviders = credentialProviders.ToList();
        _logger = logger ?? NullLogger.Instance;
        _cli = new GitCli(new GitLocator(settings, runner, baseEnvironment), runner, baseEnvironment, _logger);
    }

    private static string RepositoryDirectory(string repoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        return PathUtil.Normalize(repoPath);
    }

    private Task<GitResult> RunAsync(string repository, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        GitCommandKind kind = GitCommandKind.Read, string? standardInput = null, IReadOnlyList<string>? config = null) =>
        _cli.RunAsync(Request(repository, arguments, kind, standardInput, config), cancellationToken);

    /// <summary>Like <see cref="RunAsync"/> but hands back failures instead of throwing, for commands whose exit code is an answer.</summary>
    private Task<GitResult> ExecuteAsync(string repository, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        GitCommandKind kind = GitCommandKind.Read, string? standardInput = null, IReadOnlyList<string>? config = null) =>
        _cli.ExecuteAsync(Request(repository, arguments, kind, standardInput, config), cancellationToken);

    private static GitRequest Request(string repository, IReadOnlyList<string> arguments, GitCommandKind kind, string? standardInput, IReadOnlyList<string>? config) =>
        new()
        {
            WorkingDirectory = repository,
            Arguments = arguments,
            Kind = kind,
            StandardInput = standardInput,
            Config = config ?? [],
        };

    /// <summary>Runs one command per batch of pathspecs, keeping each command line short enough for Windows.</summary>
    private async Task RunForPathsAsync(string repository, IReadOnlyList<string> command, IEnumerable<string> relativePaths, CancellationToken cancellationToken)
    {
        foreach (var batch in GitArguments.Batch(relativePaths.Select(GitArguments.Pathspec)))
        {
            await RunAsync(repository, [.. command, "--", .. batch], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> OutputLines(GitResult result) =>
        result.StandardOutput.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}
