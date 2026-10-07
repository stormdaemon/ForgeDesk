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

    private static readonly string[] GlobalConfig = ["core.quotepath=false", "color.ui=false"];

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
        var git = await Locator.RequireAsync(cancellationToken).ConfigureAwait(false);
        var spec = BuildSpec(git.ExecutablePath, request, BaseEnvironment);

        // A local write is never killed half-way because the caller lost interest (closing a project
        // during a checkout): git couldn't remove index.lock nor finish updating the working tree.
        // Cancellation is honored until it starts; after that only the timeout stops it.
        cancellationToken.ThrowIfCancellationRequested();
        var processToken = request.Kind == GitCommandKind.Write ? CancellationToken.None : cancellationToken;

        ProcessResult processResult;
        if (request.Progress is { } progress)
        {
            var forwarder = new ProgressForwarder(progress);
            processResult = await _runner.RunAsync(spec, forwarder.OnOutput, processToken).ConfigureAwait(false);
        }
        else
        {
            processResult = await _runner.RunAsync(spec, processToken).ConfigureAwait(false);
        }

        var result = new GitResult(request.DisplayCommand, processResult.ExitCode, processResult.StandardOutput, processResult.StandardError, processResult.TimedOut);
        _logger.LogDebug("{Command} exited with {ExitCode} in {Elapsed} ms", result.Command, result.ExitCode, (long)processResult.Duration.TotalMilliseconds);
        return result.TimedOut ? throw GitErrorTranslator.Translate(result) : result;
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
