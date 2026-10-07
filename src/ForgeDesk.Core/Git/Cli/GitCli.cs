using ForgeDesk.Core.Processes;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Runs git: resolves the executable, builds a <see cref="ProcessSpec"/> (argument list, never a shell)
/// with a predictable environment, forwards progress and translates failures.
/// </summary>
internal sealed class GitCli
{
    /// <summary>Line <see cref="ProcessRunner"/> appends when it stops capturing output past the size limit.</summary>
    public const string OutputTruncatedMarker = "… [output truncated]";

    // core.fsmonitor names a program to run: a repository's own config must never start one behind the user's back.
    private static readonly string[] GlobalConfig = ["core.quotepath=false", "color.ui=false", "core.fsmonitor=false"];

    /// <summary>Reads that compare working-tree files with the index, and so may run filter drivers.</summary>
    private static readonly HashSet<string> WorkingTreeReads = new(StringComparer.Ordinal) { "status", "diff" };

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;

    /// <param name="baseEnvironment">Variables applied to every git process (tests use it to isolate the global git config).</param>
    public GitCli(GitLocator locator, IProcessRunner runner, IReadOnlyDictionary<string, string?> baseEnvironment, ILogger logger)
    {
        Locator = locator;
        _runner = runner;
        BaseEnvironment = baseEnvironment;
        _logger = logger;
    }

    public GitLocator Locator { get; }

    /// <summary>Variables every git process gets on top of ForgeDesk's own environment.</summary>
    public IReadOnlyDictionary<string, string?> BaseEnvironment { get; }

    /// <summary>Runs the command and throws a translated <see cref="Common.ForgeException"/> when it fails.</summary>
    public async Task<GitResult> RunAsync(GitRequest request, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result : throw GitErrorTranslator.Translate(result);
    }

    /// <summary>Runs the command and returns its result whatever the exit code; only timeouts throw.</summary>
    public async Task<GitResult> ExecuteAsync(GitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == GitCommandKind.Read && request.Arguments.Count > 0 && WorkingTreeReads.Contains(request.Arguments[0]))
        {
            var (repositoryKeys, failure) = await RepositoryConfigKeysAsync(request.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                // Fail closed: without knowing the repository's filters, don't run a command that may start them.
                return failure;
            }

            var overrides = GitRepositoryConfig.FilterOverrides(repositoryKeys);
            if (overrides.Count > 0)
            {
                var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal);
                foreach (var (key, value) in GitCredentialEnvironment.ToEnvironment(overrides, InheritedConfigCount()))
                {
                    environment[key] = value;
                }

                request = request with { Environment = environment };
            }
        }

        var git = await Locator.RequireAsync(cancellationToken).ConfigureAwait(false);
        var spec = BuildSpec(git.ExecutablePath, request, BaseEnvironment);

        ProcessResult processResult;
        if (request.Progress is { } progress)
        {
            var forwarder = new ProgressForwarder(progress);
            processResult = await _runner.RunAsync(spec, forwarder.OnOutput, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            processResult = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        var result = new GitResult(request.DisplayCommand, processResult.ExitCode, processResult.StandardOutput, processResult.StandardError, processResult.TimedOut);
        _logger.LogDebug("{Command} exited with {ExitCode} in {Elapsed} ms", result.Command, result.ExitCode, (long)processResult.Duration.TotalMilliseconds);
        return result.TimedOut ? throw GitErrorTranslator.Translate(result) : result;
    }

    /// <summary>Keys the repository's own configuration sets (see <see cref="GitRepositoryConfig"/>), or the failed listing.</summary>
    public async Task<(IReadOnlyList<string> Keys, GitResult? Failure)> RepositoryConfigKeysAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var listing = await ExecuteAsync(new GitRequest { WorkingDirectory = workingDirectory, Arguments = GitRepositoryConfig.ListArguments }, cancellationToken)
            .ConfigureAwait(false);
        return listing.Succeeded ? (GitRepositoryConfig.ParseRepositoryKeys(listing.StandardOutput), null) : ([], listing);
    }

    /// <summary>Number of GIT_CONFIG_* entries every git process inherits; per-command entries are appended after them.</summary>
    public int InheritedConfigCount()
    {
        var inherited = BaseEnvironment.TryGetValue(GitCredentialEnvironment.CountVariable, out var overridden)
            ? overridden
            : Environment.GetEnvironmentVariable(GitCredentialEnvironment.CountVariable);
        return GitCredentialEnvironment.InheritedConfigCount(inherited);
    }

    internal static ProcessSpec BuildSpec(string gitExecutable, GitRequest request, IReadOnlyDictionary<string, string?> baseEnvironment)
    {
        var arguments = new List<string>(request.Arguments.Count + (2 * (GlobalConfig.Length + request.Config.Count)));
        foreach (var setting in GlobalConfig.Concat(request.Config))
        {
            arguments.Add("-c");
            arguments.Add(setting);
        }

        arguments.AddRange(request.Arguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Never block on a prompt nobody can see: credentials come from helpers (GCM) or providers.
            ["GIT_TERMINAL_PROMPT"] = "0",
            // English, stable messages for GitErrorTranslator.
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
            ["LANGUAGE"] = null,
            // ":" tells git not to launch an editor; every message is passed explicitly.
            ["GIT_EDITOR"] = ":",
            ["GIT_MERGE_AUTOEDIT"] = "no",
        };

        if (request.Kind == GitCommandKind.Read)
        {
            // Reads must not take index.lock (status refreshes the index otherwise) and fight the user's own git commands.
            environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        foreach (var (key, value) in baseEnvironment)
        {
            environment[key] = value;
        }

        foreach (var (key, value) in request.Environment)
        {
            environment[key] = value;
        }

        return new ProcessSpec
        {
            FileName = gitExecutable,
            Arguments = arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = environment,
            StandardInput = request.StandardInput,
            Timeout = request.EffectiveTimeout,
            MaxCapturedChars = request.MaxOutputChars,
        };
    }

    /// <summary>Parses stderr progress lines and reports each distinct step once.</summary>
    private sealed class ProgressForwarder(IProgress<GitProgress> progress)
    {
        private readonly Lock _gate = new();
        private (string Stage, int? Percent)? _last;

        public void OnOutput(OutputLine line)
        {
            if (line.Stream != OutputStream.StandardError || !GitProgressParser.TryParse(line.Text, out var parsed))
            {
                return;
            }

            lock (_gate)
            {
                if (_last is { } last && last.Stage == parsed.Stage && last.Percent == parsed.Percent)
                {
                    return;
                }

                _last = (parsed.Stage, parsed.Percent);
            }

            progress.Report(parsed);
        }
    }
}
