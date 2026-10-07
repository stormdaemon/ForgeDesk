namespace ForgeDesk.Core.Git;

/// <summary>How a git command touches the repository; drives timeouts and locking behavior.</summary>
internal enum GitCommandKind
{
    /// <summary>Only reads: runs with GIT_OPTIONAL_LOCKS=0 so it never competes for index.lock.</summary>
    Read,

    /// <summary>Changes the local repository (may run hooks, check out many files).</summary>
    Write,

    /// <summary>Talks to a remote (fetch, pull, push).</summary>
    Network,

    /// <summary>Clones a whole repository.</summary>
    Clone,
}

/// <summary>One git invocation. Arguments start with the git subcommand ("status", "diff"…).</summary>
internal sealed record GitRequest
{
    public const int DefaultMaxOutputChars = 32 * 1024 * 1024;

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public GitCommandKind Kind { get; init; } = GitCommandKind.Read;

    /// <summary>Extra "-c key=value" settings for this command only.</summary>
    public IReadOnlyList<string> Config { get; init; } = [];

    /// <summary>Environment variables for this command only (e.g. credentials). Never logged.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string? StandardInput { get; init; }

    public IProgress<GitProgress>? Progress { get; init; }

    public int MaxOutputChars { get; init; } = DefaultMaxOutputChars;

    /// <summary>Overrides the timeout derived from <see cref="Kind"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    public TimeSpan EffectiveTimeout => Timeout ?? DefaultTimeout(Kind);

    /// <summary>"git …" with credentials in URLs masked, for error details and logs.</summary>
    public string DisplayCommand => GitRedaction.RedactCommand(Arguments);

    public static TimeSpan DefaultTimeout(GitCommandKind kind) => kind switch
    {
        GitCommandKind.Read => TimeSpan.FromSeconds(60),
        GitCommandKind.Write => TimeSpan.FromMinutes(10),
        GitCommandKind.Network => TimeSpan.FromMinutes(10),
        GitCommandKind.Clone => TimeSpan.FromHours(1),
        _ => TimeSpan.FromMinutes(10),
    };
}

/// <summary>Outcome of a git invocation.</summary>
internal sealed record GitResult(string Command, int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>True when the process runner dropped part of stdout because it exceeded the capture limit.</summary>
    /// <remarks>Line-ending agnostic: a runner may end the marker line with "\r\n" (Environment.NewLine on Windows).</remarks>
    public bool IsOutputTruncated =>
        StandardOutput.EndsWith('\n') && StandardOutput.AsSpan().TrimEnd("\r\n").EndsWith(GitCli.OutputTruncatedMarker, StringComparison.Ordinal);
}
