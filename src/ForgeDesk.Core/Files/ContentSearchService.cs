using System.Diagnostics;
using System.Globalization;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Files;

/// <summary>
/// "Search in files". Uses git grep (fast, respects .gitignore, skips binaries) — inside
/// repositories on tracked and untracked files, elsewhere with --no-index — and falls back to a
/// managed parallel search when git is unavailable or fails. Results stream to the caller; the
/// search stops (and git is killed) as soon as MaxResults is reached or the caller cancels.
/// </summary>
internal sealed class ContentSearchService : IContentSearchService
{
    public const string GitEngine = "git grep";
    public const string GitNoIndexEngine = "git grep (no index)";
    public const string ManagedEngine = "managed";

    private const int DefaultMaxResults = 2000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    private readonly GitCli _git;
    private readonly ManagedContentSearcher _managed;
    private readonly ILogger<ContentSearchService> _logger;
    private volatile bool _perlRegexUnsupported;

    public ContentSearchService(GitCli git, ILogger<ContentSearchService>? logger = null)
    {
        _git = git;
        _logger = logger ?? NullLogger<ContentSearchService>.Instance;
        _managed = new ManagedContentSearcher(_logger);
    }

    /// <summary>Disables git (tests of the managed engine, or machines where git misbehaves).</summary>
    internal bool UseGit { get; init; } = true;

    public async Task<ContentSearchSummary> SearchAsync(string projectRoot, ContentSearchQuery query, Action<ContentMatch> onMatch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(onMatch);
        var pattern = SearchPattern.Create(query);

        // Starting git and probing folders is synchronous work: keep it off the caller's (UI) thread.
        return await Task.Run(() => SearchCoreAsync(projectRoot, pattern, onMatch, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContentSearchSummary> SearchCoreAsync(string projectRoot, SearchPattern pattern, Action<ContentMatch> onMatch, CancellationToken cancellationToken)
    {
        var query = pattern.Query;
        var root = PathUtil.Normalize(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{root}' does not exist.",
                "The project may have been moved or deleted.");
        }

        var filter = PathFilter.Parse(query.PathFilter);
        var maxResults = query.MaxResults > 0 ? query.MaxResults : DefaultMaxResults;
        var stopwatch = Stopwatch.StartNew();

        if (UseGit)
        {
            var inRepository = GitCli.IsInsideRepository(root);
            var outcome = await TryGitGrepAsync(root, pattern, filter, inRepository, maxResults, onMatch, cancellationToken).ConfigureAwait(false);
            if (outcome is { } done)
            {
                return new ContentSearchSummary(done.Matches, done.Files, done.Truncated, stopwatch.Elapsed,
                    inRepository ? GitEngine : GitNoIndexEngine);
            }
        }

        var managed = await _managed.SearchAsync(root, pattern, filter, maxResults, onMatch, cancellationToken).ConfigureAwait(false);
        return new ContentSearchSummary(managed.Matches, managed.Files, managed.Truncated, stopwatch.Elapsed, ManagedEngine);
    }

    /// <summary>Returns null when git could not do the search (not installed, failed before any result).</summary>
    private async Task<SearchOutcome?> TryGitGrepAsync(string root, SearchPattern pattern, PathFilter filter, bool inRepository, int maxResults,
        Action<ContentMatch> onMatch, CancellationToken cancellationToken)
    {
        var useFixedStrings = !pattern.Query.IsRegex;
        var usePerl = !useFixedStrings && !_perlRegexUnsupported;
        while (true)
        {
            var collector = new GitGrepCollector(pattern, maxResults, onMatch);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            collector.LimitReached += () => limit.Cancel();

            var arguments = BuildArguments(pattern.Query, filter, inRepository, useFixedStrings ? "-F" : usePerl ? "-P" : "-E");
            ProcessResult? result;
            try
            {
                result = await _git.TryStreamAsync(root, arguments, collector.OnLine, limit.Token, GitTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && collector.Truncated)
            {
                return collector.Outcome;
            }

            if (result is null)
            {
                return null;
            }

            // 0: matches, 1: no match. Anything else is an error, possibly after some results.
            if (result.ExitCode is 0 or 1 && !result.TimedOut)
            {
                return collector.Outcome;
            }

            if (usePerl && collector.Matches == 0 && IsPerlUnsupported(result.StandardError))
            {
                _perlRegexUnsupported = true;
                usePerl = false;
                continue;
            }

            _logger.LogInformation("git grep failed in {Root} (exit {ExitCode}): {Error}", root, result.ExitCode, result.StandardError.Trim());
            return collector.Matches > 0 ? collector.Outcome with { Truncated = true } : null;
        }
    }

    private static List<string> BuildArguments(ContentSearchQuery query, PathFilter filter, bool inRepository, string patternMode)
    {
        var arguments = new List<string> { "grep", "-n", "--column", "-I", "--no-color", "--null" };
        if (inRepository)
        {
            arguments.Add("--untracked");
        }
        else
        {
            arguments.Add("--no-index");
            arguments.Add("--exclude-standard");
        }

        if (!query.MatchCase)
        {
            arguments.Add("-i");
        }

        if (query.WholeWord)
        {
            arguments.Add("-w");
        }

        arguments.Add(patternMode);
        arguments.Add("-e");
        arguments.Add(query.Pattern);
        arguments.Add("--");
        arguments.AddRange(filter.ToGitPathspecs());
        if (!inRepository)
        {
            // Without a repository there is no .gitignore to skip dependencies and build output.
            var magic = OperatingSystem.IsWindows() ? "exclude,glob,icase" : "exclude,glob";
            arguments.AddRange(HeavyFolders.Names.Select(name => $":({magic})**/{name}/**"));
        }

        return arguments;
    }

    private static bool IsPerlUnsupported(string standardError) =>
        standardError.Contains("Perl-compatible", StringComparison.OrdinalIgnoreCase)
        || standardError.Contains("PCRE", StringComparison.Ordinal);

    /// <summary>Parses "path\0line\0column\0text" lines and forwards them until the limit.</summary>
    private sealed class GitGrepCollector(SearchPattern pattern, int maxResults, Action<ContentMatch> onMatch)
    {
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private int _matches;
        private bool _truncated;

        public event Action? LimitReached;

        public int Matches => _matches;

        public bool Truncated => _truncated;

        public SearchOutcome Outcome => new(_matches, _files.Count, _truncated);

        public void OnLine(string line)
        {
            if (_truncated)
            {
                return;
            }

            var first = line.IndexOf('\0', StringComparison.Ordinal);
            var second = first < 0 ? -1 : line.IndexOf('\0', first + 1);
            var third = second < 0 ? -1 : line.IndexOf('\0', second + 1);
            if (third < 0
                || !int.TryParse(line.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var lineNumber)
                || !int.TryParse(line.AsSpan(second + 1, third - second - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var byteColumn))
            {
                // A fragment of a line containing a bare "\r", split by the reader: nothing reliable to show.
                return;
            }

            var path = line[..first].Replace('\\', '/');
            onMatch(pattern.CreateMatch(path, lineNumber, line[(third + 1)..], byteColumn));
            _files.Add(path);
            if (++_matches >= maxResults)
            {
                _truncated = true;
                LimitReached?.Invoke();
            }
        }
    }
}

internal readonly record struct SearchOutcome(int Matches, int Files, bool Truncated);
