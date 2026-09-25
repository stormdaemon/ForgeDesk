using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ForgeDesk.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Files;

/// <summary>
/// Per-project list of files for "Go to file". In git repositories it comes from
/// <c>git ls-files</c> (tracked + untracked, minus ignored and deleted files — fast, and
/// .gitignore-aware); elsewhere from a bounded walk that skips heavy folders. Snapshots are
/// cached per root until invalidated (the watcher does that when files change).
/// </summary>
internal sealed class FileIndex : IFileIndex
{
    public const int MaxFiles = 300_000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);

    private readonly GitCli _git;
    private readonly IClock _clock;
    private readonly ILogger<FileIndex> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<FileIndexSnapshot>>> _cache = new(PathUtil.Comparer);
    private readonly ConditionalWeakTable<FileIndexSnapshot, FuzzyMatcher> _matchers = new();

    public FileIndex(GitCli git, IClock? clock = null, ILogger<FileIndex>? logger = null)
    {
        _git = git;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<FileIndex>.Instance;
    }

    /// <summary>Lets tests exercise the managed walker inside repositories.</summary>
    internal bool UseGit { get; init; } = true;

    public async Task<FileIndexSnapshot> GetAsync(string projectRoot, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = PathUtil.Normalize(projectRoot);
        if (forceRefresh)
        {
            _cache.TryRemove(root, out _);
        }

        // Task.Run: building starts processes and walks folders synchronously; callers are often on the UI thread.
        var entry = _cache.GetOrAdd(root, static (key, self) => new Lazy<Task<FileIndexSnapshot>>(() => Task.Run(() => self.BuildAsync(key))), this);
        try
        {
            // The build is shared by every caller: one caller giving up must not cancel it for the others.
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _cache.TryRemove(KeyValuePair.Create(root, entry));
            throw;
        }
    }

    public void Invalidate(string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            _cache.TryRemove(PathUtil.Normalize(projectRoot), out _);
        }
    }

    public IReadOnlyList<FileMatch> Search(FileIndexSnapshot snapshot, string query, int max = 100)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var matcher = _matchers.GetValue(snapshot, static s => FuzzyMatcher.Create(s.Files));
        return matcher.Search(query, max);
    }

    private async Task<FileIndexSnapshot> BuildAsync(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{root}' does not exist.",
                "The project may have been moved or deleted.");
        }

        if (UseGit && GitCli.IsInsideRepository(root))
        {
            var listed = await TryListWithGitAsync(root).ConfigureAwait(false);
            if (listed is { } result)
            {
                return CreateSnapshot(root, result.Files, result.Truncated);
            }
        }

        var walked = Walk(root);
        return CreateSnapshot(root, walked.Files, walked.Truncated);
    }

    private FileIndexSnapshot CreateSnapshot(string root, List<string> files, bool truncated)
    {
        files.Sort(StringComparer.Ordinal);
        var snapshot = new FileIndexSnapshot(root, files, truncated, _clock.Now);

        // Prepare the matcher now, off the UI thread, rather than on the first keystroke.
        _matchers.GetValue(snapshot, static s => FuzzyMatcher.Create(s.Files));
        return snapshot;
    }

    private async Task<(List<string> Files, bool Truncated)?> TryListWithGitAsync(string root)
    {
        // Untracked, non-ignored files in heavy folders mean a missing .gitignore entry (node_modules…):
        // leave them out at the source. Tracked files are always the user's own content, even inside a
        // folder named "build" or "packages", so that listing is not filtered.
        var magic = OperatingSystem.IsWindows() ? "exclude,glob,icase" : "exclude,glob";
        string[] untrackedArguments = ["ls-files", "--others", "--exclude-standard", "--", .. HeavyFolders.Names.Select(n => $":({magic})**/{n}/**")];
        var tracked = ListGitPathsAsync(root, ["ls-files", "--cached"], MaxFiles + 1);
        var untracked = ListGitPathsAsync(root, untrackedArguments, MaxFiles + 1);
        var deleted = ListGitPathsAsync(root, ["ls-files", "--deleted"], MaxFiles + 1);
        await Task.WhenAll(tracked, untracked, deleted).ConfigureAwait(false);
        if (tracked.Result is not { } trackedPaths || untracked.Result is not { } untrackedPaths || deleted.Result is not { } deletedPaths)
        {
            return null;
        }

        var removed = new HashSet<string>(deletedPaths, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>(Math.Min(trackedPaths.Count + untrackedPaths.Count, MaxFiles));
        var truncated = false;

        foreach (var paths in new[] { trackedPaths, untrackedPaths })
        {
            foreach (var path in paths)
            {
                if (removed.Contains(path) || !seen.Add(path))
                {
                    continue;
                }

                if (files.Count == MaxFiles)
                {
                    truncated = true;
                    break;
                }

                files.Add(path);
            }
        }

        return (files, truncated);
    }

    /// <summary>Streams one git ls-files listing; null when git is unavailable or fails.</summary>
    private async Task<List<string>?> ListGitPathsAsync(string root, string[] arguments, int cap)
    {
        var paths = new List<string>();
        var gate = new Lock();
        using var enough = new CancellationTokenSource();
        try
        {
            var result = await _git.TryStreamAsync(root, arguments, line =>
            {
                lock (gate)
                {
                    if (line.Length == 0 || paths.Count >= cap)
                    {
                        return;
                    }

                    paths.Add(GitCli.UnquotePath(line));
                    if (paths.Count >= cap)
                    {
                        enough.Cancel();
                    }
                }
            }, enough.Token, GitTimeout).ConfigureAwait(false);

            if (result is null || result.ExitCode != 0)
            {
                if (result is not null)
                {
                    _logger.LogInformation("git {Command} failed in {Root}: {Error}", string.Join(' ', arguments), root, result.StandardError.Trim());
                }

                return null;
            }
        }
        catch (OperationCanceledException) when (enough.IsCancellationRequested)
        {
            // Stopped on purpose: the cap was reached.
        }

        lock (gate)
        {
            return [.. paths];
        }
    }

    private static (List<string> Files, bool Truncated) Walk(string root)
    {
        var files = new List<string>();
        foreach (var path in ProjectFileWalker.EnumerateFiles(root))
        {
            if (files.Count == MaxFiles)
            {
                return (files, true);
            }

            files.Add(path);
        }

        return (files, false);
    }
}
